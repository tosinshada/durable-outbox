using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DurableOutbox.Tests;

/// <summary>
/// Runs the real workers against the test's database, registered as a service would, with
/// <see cref="TestHandler"/> for every kind. The clock only moves when the test moves it, and only while
/// the workers are idle, so nothing here waits on real time.
/// </summary>
public abstract class WorkerTestBase(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    protected const string OtherKind = "invoice.file";
    protected static readonly TimeSpan Step = TimeSpan.FromSeconds(1);
    private const int MaxSteps = 1_000;

    private readonly PostgresFixture _postgres = postgres;
    private ServiceProvider? _workerServices;
    private IHostedService[] _hosted = [];
    private bool _running;

    protected HandlerScript Handler { get; } = new();
    protected CapturingLoggerProvider Logs { get; } = new();

    protected async Task StartWorkersAsync(Action<OutboxOptions> configure)
    {
        _workerServices = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddSingleton(Handler)
            .AddLogging(logging => logging.AddProvider(Logs))
            .AddDbContext<TestDbContext>(options => options.UseNpgsql(ConnectionString))
            .AddDurableOutbox<TestDbContext>(configure)
            .BuildServiceProvider();

        _hosted = [.. _workerServices.GetServices<IHostedService>()];
        _running = true;
        foreach (var hosted in _hosted)
        {
            await hosted.StartAsync(Ct);
        }
    }

    /// <summary>Stops the workers. The returned task completes once they have drained and written every outcome.</summary>
    protected async Task StopWorkersAsync()
    {
        _running = false;
        foreach (var hosted in _hosted)
        {
            await hosted.StopAsync(Ct);
        }
    }

    protected Task SetDatabaseReachableAsync(bool reachable) =>
        _postgres.SetConnectionsAllowedAsync(ConnectionString, reachable, Ct);

    /// <summary>Waits until every loop is waiting on the clock and no attempt is starting or writing its outcome.</summary>
    protected Task IdleAsync() =>
        Task.WhenAll(_hosted.OfType<OutboxWorker<TestDbContext>>().Select(worker => worker.IdleAsync())).WaitAsync(Ct);

    /// <summary>Moves the clock in steps, each once the workers are idle, until <paramref name="signal"/> completes.</summary>
    protected async Task AdvanceUntilAsync(Task signal, TimeSpan step)
    {
        for (var steps = 0; !signal.IsCompleted; steps++)
        {
            await IdleAsync();
            if (signal.IsCompleted)
            {
                break;
            }

            Advance(step, steps);
            await Task.Yield();
        }

        await signal;
    }

    /// <summary>Moves the clock in steps, each once the workers are idle, until <paramref name="condition"/> holds.</summary>
    protected async Task AdvanceUntilAsync(Func<Task<bool>> condition, TimeSpan step)
    {
        for (var steps = 0; ; steps++)
        {
            await IdleAsync();
            if (await condition())
            {
                return;
            }

            Advance(step, steps);
        }
    }

    /// <summary>Fails a test whose signal never comes, on the fake clock, instead of letting it run forever.</summary>
    private void Advance(TimeSpan step, int stepsTaken)
    {
        Assert.True(stepsTaken < MaxSteps, $"Nothing happened after moving the clock {MaxSteps} times by {step}.");
        Time.Advance(step);
    }

    /// <summary>Moves the clock forward by <paramref name="by"/>, in steps taken while the workers are idle.</summary>
    protected async Task AdvanceAsync(TimeSpan by, TimeSpan step)
    {
        var until = Time.GetUtcNow() + by;
        while (Time.GetUtcNow() < until)
        {
            await IdleAsync();
            var left = until - Time.GetUtcNow();
            Time.Advance(left < step ? left : step);
            await Task.Yield();
        }

        await IdleAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_running)
        {
            // A test that failed part-way: stop without waiting for the drain.
            foreach (var hosted in _hosted)
            {
                await hosted.StopAsync(new CancellationToken(canceled: true));
            }
        }

        if (_workerServices is not null)
        {
            await _workerServices.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}
