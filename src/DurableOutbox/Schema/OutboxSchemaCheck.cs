using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DurableOutbox;

/// <summary>
/// Compares the outbox table of <typeparamref name="TContext"/> with what the library expects when the host
/// starts, and opens the workers' gate when it matches. A mismatch fails the service, which stops the host;
/// an unreachable database is retried with the same backoff as the worker loop.
/// </summary>
internal sealed class OutboxSchemaCheck<TContext>(
    IServiceScopeFactory scopes,
    OutboxSchemaGate<TContext> gate,
    OutboxRegistration<TContext> registration,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService where TContext : DbContext
{
    private readonly ILogger _logger = loggers.CreateLogger("DurableOutbox");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var failures = 1; ; failures++)
        {
            OutboxSql sql;
            List<string> differences;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TContext>();
                sql = scope.ServiceProvider.GetRequiredService<OutboxSqlProvider<TContext>>().Get(context);
                differences = await OutboxSchema.FindDifferencesAsync(context, sql, stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                var delay = Backoff.Outage(failures, registration.Settings.PollInterval);
                OutboxLog.SchemaCheckUnreachable(_logger, typeof(TContext).Name, delay, exception);

                // The timer exists before the check counts as waiting, so a clock moved after that is seen by it.
                var wait = Task.Delay(delay, time, stoppingToken);
                gate.BeginWait();
                try
                {
                    await wait;
                }
                finally
                {
                    gate.EndWait();
                }

                continue;
            }

            if (differences.Count == 0)
            {
                gate.Pass();
                return;
            }

            OutboxLog.SchemaMismatch(_logger, sql.Table, string.Join("; ", differences));
            var mismatch = OutboxSchema.Mismatch(sql, differences);
            gate.Fail(mismatch);
            throw mismatch;
        }
    }
}
