namespace DurableOutbox;

/// <summary>
/// The library's statements for one outbox table. Values are always parameters; status names are
/// written in the text so the planner can match the partial index.
/// </summary>
internal sealed class OutboxSql
{
    /// <summary>The error history keeps this many entries, the most recent.</summary>
    public const int ErrorHistoryLimit = 20;

    /// <summary>
    /// True when the row has no attempts left, by count or by age. The retry window applies only once the
    /// message has been attempted, so a message with no attempts (new, or reset by an operator) is claimed.
    /// </summary>
    private const string Spent = "attempts >= @max_attempts OR (attempts > 0 AND created_at <= @give_up_before)";

    public OutboxSql(string table)
    {
        Insert = $"""
            INSERT INTO {table}
                   (id, kind, record_ref, unique_key, priority, status, attempts,
                    next_attempt_at, deadline_at, created_at, errors, trace_parent)
            VALUES (@id, @kind, @record_ref, @unique_key, @priority, 'Pending', 0,
                    @now, @deadline_at, @now, '[]'::jsonb, @trace_parent)
            ON CONFLICT (kind, unique_key) WHERE unique_key IS NOT NULL DO NOTHING
            """;

        Claim = $"""
            WITH due AS MATERIALIZED (
                SELECT id,
                       CASE
                           WHEN deadline_at <= @now THEN 'Expired'
                           WHEN {Spent} THEN 'Parked'
                           ELSE 'Pending'
                       END AS verdict,
                       lease_token IS NOT NULL AS abandoned
                  FROM {table}
                 WHERE status = 'Pending' AND kind = @kind AND next_attempt_at <= @now
                 ORDER BY priority DESC, next_attempt_at
                 LIMIT @limit
                   FOR UPDATE SKIP LOCKED
            )
            UPDATE {table} m
               SET status          = due.verdict,
                   attempts        = m.attempts + (due.verdict = 'Pending')::int,
                   lease_token     = CASE WHEN due.verdict = 'Pending' THEN @token END,
                   next_attempt_at = CASE WHEN due.verdict = 'Pending' THEN @now + @lease ELSE m.next_attempt_at END,
                   finished_at     = CASE WHEN due.verdict <> 'Pending' THEN @now END,
                   last_error      = CASE WHEN due.abandoned THEN @error ELSE m.last_error END,
                   errors          = CASE WHEN due.abandoned THEN {AppendError("m.")} ELSE m.errors END
              FROM due
             WHERE m.id = due.id
            RETURNING m.id AS "Id", m.record_ref AS "RecordRef", m.status AS "Status", m.attempts AS "Attempt",
                      m.created_at AS "CreatedAt", m.deadline_at AS "Deadline", m.trace_parent AS "TraceParent",
                      due.abandoned AS "Abandoned"
            """;

        ClaimHolding = $"""
            WITH due AS MATERIALIZED (
                SELECT id,
                       CASE
                           WHEN deadline_at <= @now THEN 'Expired'
                           WHEN {Spent} THEN 'Parked'
                           ELSE 'Pending'
                       END AS verdict,
                       lease_token IS NOT NULL AS abandoned
                  FROM {table}
                 WHERE status = 'Pending' AND kind = @kind AND next_attempt_at <= @now
                 ORDER BY priority DESC, next_attempt_at
                 LIMIT @limit
                   FOR UPDATE SKIP LOCKED
            )
            UPDATE {table} m
               SET attempts        = m.attempts + (due.verdict = 'Pending')::int,
                   lease_token     = @token,
                   next_attempt_at = @now + @lease,
                   last_error      = CASE WHEN due.abandoned THEN @error ELSE m.last_error END,
                   errors          = CASE WHEN due.abandoned THEN {AppendError("m.")} ELSE m.errors END
              FROM due
             WHERE m.id = due.id
            RETURNING m.id AS "Id", m.record_ref AS "RecordRef", due.verdict AS "Status", m.attempts AS "Attempt",
                      m.created_at AS "CreatedAt", m.deadline_at AS "Deadline", m.trace_parent AS "TraceParent",
                      due.abandoned AS "Abandoned"
            """;

        Complete = $"""
            UPDATE {table}
               SET status = 'Completed', finished_at = @now, lease_token = NULL
             WHERE id = @id AND lease_token = @token AND status = 'Pending'
            """;

        Fail = $"""
            UPDATE {table}
               SET status          = CASE WHEN {Spent} THEN 'Parked' ELSE 'Pending' END,
                   finished_at     = CASE WHEN {Spent} THEN @now END,
                   next_attempt_at = CASE WHEN {Spent} THEN next_attempt_at ELSE @next END,
                   lease_token     = NULL,
                   last_error      = @error,
                   errors          = {AppendError("")}
             WHERE id = @id AND lease_token = @token AND status = 'Pending'
            RETURNING status AS "Value"
            """;

        FailHolding = $"""
            UPDATE {table}
               SET next_attempt_at = CASE WHEN {Spent} THEN @now + @lease ELSE @next END,
                   lease_token     = CASE WHEN {Spent} THEN lease_token END,
                   last_error      = @error,
                   errors          = {AppendError("")}
             WHERE id = @id AND lease_token = @token AND status = 'Pending'
            RETURNING CASE WHEN {Spent} THEN 'Parked' ELSE 'Pending' END AS "Value"
            """;

        EndParked = End(table, OutboxStatus.Parked);
        EndExpired = End(table, OutboxStatus.Expired);

        Reject = $"""
            UPDATE {table}
               SET status = 'Rejected', finished_at = @now, lease_token = NULL,
                   last_error = @error,
                   errors = {AppendError("")}
             WHERE id = @id AND lease_token = @token AND status = 'Pending'
            """;

        Renew = $"""
            WITH held AS MATERIALIZED (
                SELECT m.id
                  FROM {table} m
                  JOIN unnest(@ids, @tokens) AS h(id, token) ON m.id = h.id AND m.lease_token = h.token
                 WHERE m.status = 'Pending'
                   FOR UPDATE OF m SKIP LOCKED
            )
            UPDATE {table} m
               SET next_attempt_at = @now + @lease
              FROM held
             WHERE m.id = held.id
            RETURNING m.id AS "Value"
            """;

        Retry = $"""
            UPDATE {table}
               SET status = 'Pending', attempts = 0, next_attempt_at = @now, lease_token = NULL, finished_at = NULL
             WHERE id = @id AND status IN ('Parked', 'Rejected')
            """;

        Cancel = $"""
            UPDATE {table}
               SET status = 'Cancelled', finished_at = @now, lease_token = NULL
             WHERE id = @id AND status IN ('Pending', 'Parked')
            """;

        Sample = $"""
            SELECT kind AS "Kind", count(*) AS "Pending", 0::bigint AS "Parked",
                   min(next_attempt_at) FILTER (WHERE next_attempt_at <= @now) AS "OldestDue"
              FROM {table} WHERE status = 'Pending' GROUP BY kind
            UNION ALL
            SELECT kind, 0, count(*), NULL
              FROM {table} WHERE status = 'Parked' GROUP BY kind
            """;

        Find = $"""
            SELECT {InfoColumns}
              FROM {table}
             WHERE id = @id
            """;

        Table = table;
    }

    /// <summary>The schema-qualified table name, delimited as the SQL writes it.</summary>
    public string Table { get; }

    public string Insert { get; }
    public string Claim { get; }

    /// <summary>
    /// The claim for a kind with a give-up hook. The same statement as <see cref="Claim"/>, except that a row
    /// whose verdict is <c>Parked</c> or <c>Expired</c> is not ended: it stays pending under the claim's token
    /// and lease, with no attempt counted, and the verdict is returned as its status for the worker to end it.
    /// </summary>
    public string ClaimHolding { get; }

    public string Complete { get; }
    public string Fail { get; }

    /// <summary>
    /// The failed attempt for a kind with a give-up hook. It records the error and chooses as <see cref="Fail"/>
    /// does, but a spent row is not parked: it stays pending under the attempt's token for a full lease, and
    /// <c>Parked</c> is returned as the verdict for the worker to end it.
    /// </summary>
    public string FailHolding { get; }

    /// <summary>Ends a held message as <c>Parked</c>, fenced. A non-null <c>@error</c> is recorded with it.</summary>
    public string EndParked { get; }

    /// <summary>Ends a held message as <c>Expired</c>, fenced. A non-null <c>@error</c> is recorded with it.</summary>
    public string EndExpired { get; }
    public string Reject { get; }

    /// <summary>
    /// Extends the lease of every message still held under its own claim's token. Skips rows another
    /// statement has locked; a row skipped counts as not renewed.
    /// </summary>
    public string Renew { get; }

    /// <summary>The gauges' figures per kind, each half served by one partial index. A live claim is not due.</summary>
    public string Sample { get; }

    /// <summary>Operator retry: <c>Parked</c> or <c>Rejected</c> back to <c>Pending</c>, attempts reset, errors kept.</summary>
    public string Retry { get; }

    /// <summary>Operator cancel. Clearing the token refuses the outcome of an attempt that holds the message.</summary>
    public string Cancel { get; }

    /// <summary>
    /// The operator list for one status, newest finished first. <paramref name="status"/> is written into
    /// the text, never passed as a parameter, so the planner can match the operator index's filter; callers
    /// pass only <see cref="OutboxStatus.Parked"/> or <see cref="OutboxStatus.Rejected"/>.
    /// </summary>
    public string List(string status, bool byKind, bool afterEntry) => $"""
        SELECT {InfoColumns}
          FROM {Table}
         WHERE status = '{status}'
               {(byKind ? "AND kind = @kind" : "")}
               {(afterEntry ? "AND (finished_at, id) < (@after_finished_at, @after_id)" : "")}
         ORDER BY finished_at DESC, id DESC
         LIMIT @limit
        """;

    /// <summary>The operator read of one message by primary key, in any status, with the list's columns.</summary>
    public string Find { get; }

    /// <summary>
    /// The columns of an operator entry, named as <see cref="OutboxListRow"/> reads them. The list and the
    /// read both select these, so an entry is the same whichever returns it.
    /// </summary>
    private const string InfoColumns = """
        id AS "Id", kind AS "Kind", record_ref AS "RecordRef", unique_key AS "UniqueKey",
               priority AS "Priority", status AS "Status", attempts AS "Attempts", created_at AS "CreatedAt",
               deadline_at AS "Deadline", finished_at AS "FinishedAt", last_error AS "LastError", errors AS "Errors"
        """;

    private static string End(string table, string status) => $"""
        UPDATE {table}
           SET status = '{status}', finished_at = @now, lease_token = NULL,
               last_error = COALESCE(@error, last_error),
               errors = CASE WHEN @error IS NULL THEN errors ELSE {AppendError("")} END
         WHERE id = @id AND lease_token = @token AND status = 'Pending'
        """;

    /// <summary>
    /// The row's error history with one entry added for its current attempt, cut to the most recent
    /// <see cref="ErrorHistoryLimit"/>. Every statement that records an error uses it.
    /// </summary>
    private static string AppendError(string row) => $"""
        (SELECT jsonb_agg(entry ORDER BY position)
                   FROM (SELECT entry, position
                           FROM jsonb_array_elements(
                                    {row}errors || jsonb_build_object('attempt', {row}attempts, 'at', @now, 'error', @error))
                                WITH ORDINALITY AS history(entry, position)
                          ORDER BY position DESC
                          LIMIT {ErrorHistoryLimit}) AS recent)
        """;
}
