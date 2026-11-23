using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DurableOutbox;

/// <summary>
/// The claim and the fenced outcomes. Every outcome applies only while the row is pending under the
/// token of the claim that reports it; otherwise the lease was lost and nothing changes.
/// </summary>
internal sealed class OutboxStore<TContext>(TContext context, OutboxSqlProvider<TContext> sql, TimeProvider time)
    where TContext : DbContext
{
    internal const string AbandonedError = "The attempt did not report before its lease ended.";

    public async Task<ClaimBatch> ClaimAsync(
        string kind, int limit, TimeSpan lease, int maxAttempts, TimeSpan? retryWindow, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var token = Guid.NewGuid();

        var messages = await context.Database
            .SqlQueryRaw<ClaimedMessage>(
                sql.Get(context).Claim,
                new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = kind },
                new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit },
                new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = token },
                new NpgsqlParameter("lease", NpgsqlDbType.Interval) { Value = lease },
                Error(AbandonedError),
                Now(now),
                MaxAttempts(maxAttempts),
                GiveUpBefore(now, retryWindow))
            .ToListAsync(cancellationToken);

        return new ClaimBatch(token, messages);
    }

    public async Task<bool> CompleteAsync(Guid id, Guid token, CancellationToken cancellationToken)
    {
        var updated = await context.Database.ExecuteSqlRawAsync(
            sql.Get(context).Complete,
            [Id(id), Token(token), Now(time.GetUtcNow())],
            cancellationToken);

        return updated == 1;
    }

    /// <summary>Retries at <paramref name="nextAttemptAt"/>, or parks when the attempts or the retry window are used up.</summary>
    public async Task<FailOutcome> FailAsync(
        Guid id, Guid token, string error, DateTimeOffset nextAttemptAt, int maxAttempts, TimeSpan? retryWindow,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        var statuses = await context.Database
            .SqlQueryRaw<string>(
                sql.Get(context).Fail,
                Id(id),
                Token(token),
                Now(now),
                Error(error),
                new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = nextAttemptAt.ToUniversalTime() },
                MaxAttempts(maxAttempts),
                GiveUpBefore(now, retryWindow))
            .ToListAsync(cancellationToken);

        return statuses switch
        {
            [] => FailOutcome.Refused,
            [OutboxStatus.Parked] => FailOutcome.Parked,
            _ => FailOutcome.Retried,
        };
    }

    public async Task<bool> RejectAsync(Guid id, Guid token, string reason, CancellationToken cancellationToken)
    {
        var updated = await context.Database.ExecuteSqlRawAsync(
            sql.Get(context).Reject,
            [Id(id), Token(token), Now(time.GetUtcNow()), Error(reason)],
            cancellationToken);

        return updated == 1;
    }

    /// <summary>
    /// Extends the lease of each message whose token is still the one given for it, and returns the IDs
    /// it extended. <paramref name="ids"/> and <paramref name="tokens"/> are pairs.
    /// </summary>
    public async Task<List<Guid>> RenewAsync(Guid[] ids, Guid[] tokens, TimeSpan lease, CancellationToken cancellationToken) =>
        await context.Database
            .SqlQueryRaw<Guid>(
                sql.Get(context).Renew,
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids },
                new NpgsqlParameter("tokens", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = tokens },
                new NpgsqlParameter("lease", NpgsqlDbType.Interval) { Value = lease },
                Now(time.GetUtcNow()))
            .ToListAsync(cancellationToken);

    private static NpgsqlParameter Id(Guid id) => new("id", NpgsqlDbType.Uuid) { Value = id };

    private static NpgsqlParameter Token(Guid token) => new("token", NpgsqlDbType.Uuid) { Value = token };

    private static NpgsqlParameter Now(DateTimeOffset now) => new("now", NpgsqlDbType.TimestampTz) { Value = now };

    private static NpgsqlParameter Error(string error) => new("error", NpgsqlDbType.Text) { Value = error };

    private static NpgsqlParameter MaxAttempts(int maxAttempts) =>
        new("max_attempts", NpgsqlDbType.Integer) { Value = maxAttempts };

    /// <summary>A message created at or before this time is past its retry window. Null when the kind has none.</summary>
    private static NpgsqlParameter GiveUpBefore(DateTimeOffset now, TimeSpan? retryWindow) =>
        new("give_up_before", NpgsqlDbType.TimestampTz) { Value = retryWindow is { } window ? now - window : DBNull.Value };
}
