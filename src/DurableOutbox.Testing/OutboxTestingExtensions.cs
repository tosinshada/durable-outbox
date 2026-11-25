using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DurableOutbox.Testing;

/// <summary>Helpers for a service's own integration tests.</summary>
public static class OutboxTestingExtensions
{
    /// <summary>
    /// Runs the handlers registered for <typeparamref name="TContext"/>, with their options, until no message of
    /// a registered kind is due and no attempt is running, then returns with every outcome written. Works when
    /// <see cref="OutboxOptions.RunWorkers"/> is false. Uses the registered <see cref="TimeProvider"/> and does
    /// not move it, so a message whose next attempt is in the future is left for later.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No handler is registered for <typeparamref name="TContext"/>, or its outbox table does not match what the
    /// library expects.
    /// </exception>
    public static async Task RunUntilIdleAsync<TContext>(this IServiceProvider services, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var registration = services.GetService<OutboxRegistration<TContext>>();
        if (registration is null || registration.Settings.Kinds.Count == 0)
        {
            throw new InvalidOperationException(
                $"No outbox handler is registered for {typeof(TContext).Name}. "
                + $"Register one with services.AddDurableOutbox<{typeof(TContext).Name}>(outbox => outbox.AddHandler<...>(kind)).");
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var sql = scope.ServiceProvider.GetRequiredService<OutboxSqlProvider<TContext>>().Get(context);
            var differences = await OutboxSchema.FindDifferencesAsync(context, sql, cancellationToken);
            if (differences.Count > 0)
            {
                throw OutboxSchema.Mismatch(sql, differences);
            }
        }

        var checkedSchema = new OutboxSchemaGate<TContext>();
        checkedSchema.Pass();

        // Each kind's loop stops on its own once nothing of its kind is due or running. A handler or a give-up
        // hook of one kind may add a message of another kind whose loop has already stopped, so the loops are
        // run again until a pass leaves nothing due.
        do
        {
            var worker = new OutboxWorker<TContext>(
                registration.Settings,
                services.GetRequiredService<IServiceScopeFactory>(),
                services.GetRequiredService<TimeProvider>(),
                OutboxServiceCollectionExtensions.Loggers(services),
                services.GetRequiredService<OutboxMetrics>(),
                checkedSchema,
                stopWhenIdle: true);

            await worker.RunLoopsAsync(cancellationToken);
        }
        while (await AnyDueAsync<TContext>(services, registration.Settings, cancellationToken));
    }

    private static async Task<bool> AnyDueAsync<TContext>(
        IServiceProvider services, OutboxSettings settings, CancellationToken cancellationToken)
        where TContext : DbContext
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var table = scope.ServiceProvider.GetRequiredService<OutboxSqlProvider<TContext>>().Get(context).Table;

        // The table name is the library's own delimited identifier, never a caller's text.
#pragma warning disable EF1002
        var due = await context.Database
            .SqlQueryRaw<bool>(
                $"""
                SELECT EXISTS (
                    SELECT 1 FROM {table}
                     WHERE status = 'Pending' AND kind = ANY(@kinds) AND next_attempt_at <= @now) AS "Value"
                """,
                new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = settings.Kinds.Select(kind => kind.Kind).ToArray() },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = services.GetRequiredService<TimeProvider>().GetUtcNow() })
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002

        return due.Single();
    }
}
