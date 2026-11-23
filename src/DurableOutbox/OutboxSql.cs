namespace DurableOutbox;

/// <summary>
/// The library's statements for one outbox table. Values are always parameters; status names are
/// written in the text so the planner can match the partial index.
/// </summary>
internal sealed class OutboxSql
{
    /// <summary>The error history keeps this many entries, the most recent.</summary>
    public const int ErrorHistoryLimit = 20;

    /// <summary>True when the row has no attempts left, by count or by age.</summary>
    private const string Spent = "attempts >= @max_attempts OR created_at <= @give_up_before";

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
                      m.created_at AS "CreatedAt", m.deadline_at AS "Deadline", m.trace_parent AS "TraceParent"
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

        Reject = $"""
            UPDATE {table}
               SET status = 'Rejected', finished_at = @now, lease_token = NULL,
                   last_error = @error,
                   errors = {AppendError("")}
             WHERE id = @id AND lease_token = @token AND status = 'Pending'
            """;
    }

    public string Insert { get; }
    public string Claim { get; }
    public string Complete { get; }
    public string Fail { get; }
    public string Reject { get; }

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
