using System.Diagnostics;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DurableOutbox;

internal sealed class Outbox<TContext>(TContext context, OutboxSqlProvider<TContext> sql, TimeProvider time) : IOutbox
    where TContext : DbContext
{
    public async Task<bool> AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.RecordRef);

        if (context.Database.CurrentTransaction is null
            && context.Database.GetEnlistedTransaction() is null
            && Transaction.Current is null)
        {
            throw new InvalidOperationException(
                "An outbox message must be added inside a transaction, so that it commits with the business change. "
                + "Open one with Database.BeginTransactionAsync before calling AddAsync.");
        }

        var now = time.GetUtcNow();

        var inserted = await context.Database.ExecuteSqlRawAsync(
            sql.Get(context).Insert,
            [
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.CreateVersion7(now) },
                new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = message.Kind },
                new NpgsqlParameter("record_ref", NpgsqlDbType.Text) { Value = message.RecordRef },
                new NpgsqlParameter("unique_key", NpgsqlDbType.Text) { Value = (object?)message.UniqueKey ?? DBNull.Value },
                new NpgsqlParameter("priority", NpgsqlDbType.Integer) { Value = message.Priority },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("deadline_at", NpgsqlDbType.TimestampTz)
                {
                    Value = (object?)message.Deadline?.ToUniversalTime() ?? DBNull.Value,
                },
                new NpgsqlParameter("trace_parent", NpgsqlDbType.Text) { Value = (object?)Activity.Current?.Id ?? DBNull.Value },
            ],
            cancellationToken);

        return inserted == 1;
    }
}
