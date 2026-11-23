using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace DurableOutbox.Testing;

/// <summary>
/// One contract call's world: the service's <typeparamref name="TContext"/>, a fake clock, and kinds no one
/// else uses, so calls can run at the same time on one database without touching the service's messages.
/// Every helper runs in its own scope, as a request or a worker would.
/// </summary>
internal sealed class ContractHost<TContext> : IAsyncDisposable where TContext : DbContext
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    private readonly ServiceProvider _services;
    private IHostedService[] _hosted = [];
    private bool _running;

    public ContractHost(Action<IServiceCollection> addDbContext, CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        Kind = $"durable-outbox.contract.{suffix}";
        OtherKind = $"durable-outbox.contract-other.{suffix}";

        var services = new ServiceCollection();
        addDbContext(services);
        _services = services
            .AddSingleton<TimeProvider>(Time)
            .AddSingleton(Script)
            .AddDurableOutbox<TContext>(outbox => outbox.AddHandler<ContractHandler>(Kind, kind => kind.Lease = Lease))
            .BuildServiceProvider();
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public ContractScript Script { get; } = new();

    /// <summary>The kind the contract writes, claims and handles.</summary>
    public string Kind { get; }

    /// <summary>A second kind of the contract's own, with no handler.</summary>
    public string OtherKind { get; }

    public CancellationToken CancellationToken { get; }

    public WorkerClock Clock => WorkerClock.For<TContext>(Time, _hosted, CancellationToken);

    public async Task StartWorkersAsync()
    {
        _hosted = [.. _services.GetServices<IHostedService>()];
        _running = true;
        foreach (var hosted in _hosted)
        {
            await hosted.StartAsync(CancellationToken);
        }
    }

    /// <summary>Stops the workers once they have drained and written every outcome.</summary>
    public async Task StopWorkersAsync()
    {
        _running = false;
        foreach (var hosted in _hosted)
        {
            await hosted.StopAsync(CancellationToken);
        }
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>Adds messages of <see cref="Kind"/> for each record in one committed transaction.</summary>
    public Task AddAsync(params string[] recordRefs) => InScopeAsync(async scope =>
    {
        var context = scope.GetRequiredService<TContext>();
        var outbox = scope.GetRequiredService<IOutbox>();
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken);
        foreach (var recordRef in recordRefs)
        {
            await outbox.AddAsync(new OutboxMessage(Kind, recordRef), CancellationToken);
        }

        await transaction.CommitAsync(CancellationToken);
        return true;
    });

    public Task<ClaimBatch> ClaimAsync(int limit = 10, int maxAttempts = 10) =>
        InScopeAsync(scope => Store(scope).ClaimAsync(Kind, limit, Lease, maxAttempts, null, CancellationToken));

    public Task<bool> CompleteAsync(Guid id, Guid token) =>
        InScopeAsync(scope => Store(scope).CompleteAsync(id, token, CancellationToken));

    public Task<FailOutcome> FailAsync(Guid id, Guid token, string error, int maxAttempts = 10) =>
        InScopeAsync(scope => Store(scope).FailAsync(id, token, error, Time.GetUtcNow(), maxAttempts, null, CancellationToken));

    public Task<bool> RejectAsync(Guid id, Guid token, string reason) =>
        InScopeAsync(scope => Store(scope).RejectAsync(id, token, reason, CancellationToken));

    /// <summary>The rows of this call's kinds, and no others.</summary>
    public Task<List<OutboxMessageRow>> RowsAsync() => InScopeAsync(scope =>
        scope.GetRequiredService<TContext>().Set<OutboxMessageRow>().AsNoTracking()
            .Where(row => row.Kind == Kind || row.Kind == OtherKind)
            .ToListAsync(CancellationToken));

    public static (int Attempt, string Error)[] Errors(OutboxMessageRow row)
    {
        using var document = JsonDocument.Parse(row.Errors);
        return [.. document.RootElement.EnumerateArray()
            .Select(entry => (entry.GetProperty("attempt").GetInt32(), entry.GetProperty("error").GetString()!))];
    }

    public async ValueTask DisposeAsync()
    {
        if (_running)
        {
            // A check failed part-way: stop without waiting for the drain.
            foreach (var hosted in _hosted)
            {
                await hosted.StopAsync(new CancellationToken(canceled: true));
            }
        }

        await _services.DisposeAsync();
    }

    private static OutboxStore<TContext> Store(IServiceProvider scope) => scope.GetRequiredService<OutboxStore<TContext>>();
}
