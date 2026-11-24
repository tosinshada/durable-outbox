using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DurableOutbox.Testing;
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

    /// <summary>A singleton of the workers' container.</summary>
    private protected T WorkerService<T>() where T : notnull => _workerServices!.GetRequiredService<T>();

    /// <summary>The started hosted service of type <typeparamref name="T"/>.</summary>
    private protected T Hosted<T>() where T : IHostedService => _hosted.OfType<T>().Single();

    protected Task SetDatabaseReachableAsync(bool reachable) =>
        _postgres.SetConnectionsAllowedAsync(ConnectionString, reachable, Ct);

    /// <summary>The kit's clock helper, over the workers and samplers this test started.</summary>
    private WorkerClock Clock => WorkerClock.For<TestDbContext>(Time, _hosted, Ct);

    /// <summary>
    /// Waits until every loop is waiting on the clock with no attempt starting or writing its outcome, and no
    /// gauge sample is being taken.
    /// </summary>
    protected Task IdleAsync() => Clock.IdleAsync();

    /// <summary>Moves the clock in steps, each once the workers are idle, until <paramref name="signal"/> completes.</summary>
    protected Task AdvanceUntilAsync(Task signal, TimeSpan step) => Clock.AdvanceUntilAsync(signal, step);

    /// <summary>Moves the clock in steps, each once the workers are idle, until <paramref name="condition"/> holds.</summary>
    protected Task AdvanceUntilAsync(Func<Task<bool>> condition, TimeSpan step) => Clock.AdvanceUntilAsync(condition, step);

    /// <summary>Moves the clock forward by <paramref name="by"/>, in steps taken while the workers are idle.</summary>
    protected Task AdvanceAsync(TimeSpan by, TimeSpan step) => Clock.AdvanceAsync(by, step);

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
