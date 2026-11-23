using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DurableOutbox;

/// <summary>
/// Runs one loop per registered kind for <typeparamref name="TContext"/> once the start-up check has passed,
/// and drains them on shutdown.
/// </summary>
internal sealed class OutboxWorker<TContext> : BackgroundService where TContext : DbContext
{
    private readonly KindLoop<TContext>[] _loops;
    private readonly OutboxSchemaGate<TContext> _gate;

    public OutboxWorker(
        OutboxSettings settings,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILoggerFactory loggers,
        OutboxMetrics metrics,
        OutboxSchemaGate<TContext> gate,
        bool stopWhenIdle = false)
    {
        var logger = loggers.CreateLogger("DurableOutbox");
        _gate = gate;
        _loops = [.. settings.Kinds.Select(kind => new KindLoop<TContext>(kind, settings, scopes, time, logger, metrics, gate.Passed, stopWhenIdle))];
    }

    /// <summary>
    /// Completes when nothing moves until the clock does: the start-up check is waiting to try again, or
    /// every loop is idle (see <see cref="KindLoop{TContext}.Idle"/>).
    /// </summary>
    internal async Task IdleAsync()
    {
        await Task.WhenAny(_gate.Passed, _gate.Waiting);
        if (_gate.Passed.IsCompleted)
        {
            await Task.WhenAll(_loops.Select(loop => loop.Idle));
        }
    }

    /// <summary>
    /// Runs every loop until <paramref name="stoppingToken"/> fires, or, for a worker built to stop when idle,
    /// until nothing of its kinds is due or running.
    /// </summary>
    internal Task RunLoopsAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_loops.Select(loop => loop.RunAsync(stoppingToken)));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunLoopsAsync(stoppingToken);
}
