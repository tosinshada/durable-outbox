using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DurableOutbox;

/// <summary>
/// Samples the gauges of <typeparamref name="TContext"/>'s table every 15 seconds with one statement. The
/// gauges and the health check read <see cref="Latest"/> and never query.
/// </summary>
internal sealed class OutboxSampler<TContext> : BackgroundService where TContext : DbContext
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private TaskCompletionSource _idle = Completed();
    private volatile OutboxSample? _latest;

    public OutboxSampler(IServiceScopeFactory scopes, TimeProvider time, ILoggerFactory loggers, OutboxMetrics metrics)
    {
        _scopes = scopes;
        _time = time;
        _logger = loggers.CreateLogger("DurableOutbox");
        metrics.AddSampler(() => _latest);
    }

    /// <summary>The last sample, or null before the first.</summary>
    public OutboxSample? Latest => _latest;

    /// <summary>Completes while no sample is being taken, so the sampler waits only on its clock.</summary>
    internal Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle.Task;
            }
        }
    }

    /// <summary>Takes one sample now. A failure keeps the last figures and marks the sample failed.</summary>
    internal async Task SampleAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource sampling;
        lock (_gate)
        {
            sampling = _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var now = _time.GetUtcNow();
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var sql = scope.ServiceProvider.GetRequiredService<OutboxSqlProvider<TContext>>().Get(context);
            var rows = await context.Database
                .SqlQueryRaw<SampleRow>(sql.Sample, new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now })
                .ToListAsync(cancellationToken);

            KindSample[] kinds = [.. rows
                .GroupBy(row => row.Kind)
                .Select(kind => new KindSample(
                    kind.Key,
                    kind.Sum(row => row.Pending),
                    kind.Sum(row => row.Parked),
                    kind.Min(row => row.OldestDue) is { } oldest ? now - oldest : TimeSpan.Zero))];
            _latest = new OutboxSample(now, kinds, Failed: false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            OutboxLog.SampleFailed(_logger, typeof(TContext).Name, exception);
            _latest = (_latest ?? new OutboxSample(now, [], Failed: true)) with { TakenAt = now, Failed = true };
        }
        finally
        {
            sampling.TrySetResult();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            do
            {
                await SampleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    private static TaskCompletionSource Completed()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }
}
