using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
}
