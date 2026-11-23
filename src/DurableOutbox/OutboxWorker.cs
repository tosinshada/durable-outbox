using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DurableOutbox;

/// <summary>Runs one loop per registered kind for <typeparamref name="TContext"/>, and drains them on shutdown.</summary>
internal sealed class OutboxWorker<TContext> : BackgroundService where TContext : DbContext
{
    private readonly KindLoop<TContext>[] _loops;

    public OutboxWorker(OutboxSettings settings, IServiceScopeFactory scopes, TimeProvider time, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("DurableOutbox");
        _loops = [.. settings.Kinds.Select(kind => new KindLoop<TContext>(kind, settings, scopes, time, logger))];
    }

    /// <summary>Completes when every loop is idle: see <see cref="KindLoop{TContext}.Idle"/>.</summary>
    internal Task IdleAsync() => Task.WhenAll(_loops.Select(loop => loop.Idle));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_loops.Select(loop => loop.RunAsync(stoppingToken)));
}
