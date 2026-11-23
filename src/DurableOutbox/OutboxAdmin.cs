using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DurableOutbox;

internal sealed class OutboxAdmin<TContext>(TContext context, OutboxSqlProvider<TContext> sql, TimeProvider time) : IOutboxAdmin
    where TContext : DbContext
{
    private const int MaxLimit = 1_000;

    public async Task<IReadOnlyList<OutboxMessageInfo>> ListAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var status = query.Status switch
        {
            OutboxMessageStatus.Parked => OutboxStatus.Parked,
            OutboxMessageStatus.Rejected => OutboxStatus.Rejected,
            _ => throw new ArgumentException(
                $"Only Parked and Rejected messages can be listed, not {query.Status}.", nameof(query)),
        };

        if (query.Limit is < 1 or > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query), query.Limit, $"The limit must be from 1 to {MaxLimit}.");
        }

        if (query.After is { FinishedAt: null })
        {
            throw new ArgumentException("The entry to continue after has no finish time.", nameof(query));
        }

        List<NpgsqlParameter> parameters = [new("limit", NpgsqlDbType.Integer) { Value = query.Limit }];
        if (query.Kind is not null)
        {
            parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = query.Kind });
        }

        if (query.After is { } after)
        {
            parameters.Add(new NpgsqlParameter("after_finished_at", NpgsqlDbType.TimestampTz) { Value = after.FinishedAt!.Value });
            parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = after.Id });
        }

        var rows = await context.Database
            .SqlQueryRaw<OutboxListRow>(
                sql.Get(context).List(status, byKind: query.Kind is not null, afterEntry: query.After is not null),
                [.. parameters])
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToInfo)];
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlRawAsync(sql.Get(context).Retry, [Id(id), Now()], cancellationToken) == 1;

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlRawAsync(sql.Get(context).Cancel, [Id(id), Now()], cancellationToken) == 1;

    private static OutboxMessageInfo ToInfo(OutboxListRow row) => new(
        row.Id,
        row.Kind,
        row.RecordRef,
        row.UniqueKey,
        row.Priority,
        Enum.Parse<OutboxMessageStatus>(row.Status),
        row.Attempts,
        row.CreatedAt,
        row.Deadline,
        row.FinishedAt,
        row.LastError,
        Errors(row.Errors));

    private static OutboxAttemptError[] Errors(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(entry => new OutboxAttemptError(
            entry.GetProperty("attempt").GetInt32(),
            entry.GetProperty("at").GetDateTimeOffset(),
            entry.GetProperty("error").GetString()!))];
    }

    private static NpgsqlParameter Id(Guid id) => new("id", NpgsqlDbType.Uuid) { Value = id };

    private NpgsqlParameter Now() => new("now", NpgsqlDbType.TimestampTz) { Value = time.GetUtcNow() };
}
