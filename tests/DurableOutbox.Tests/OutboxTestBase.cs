using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DurableOutbox.Tests;

/// <summary>
/// A migrated database per test, the outbox registered as a service would, and a clock the test moves.
/// Every helper runs in its own scope, as a request or a worker would.
/// </summary>
public abstract class OutboxTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected const string Kind = "receipt.send";
    protected static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private ServiceProvider _services = null!;

    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await postgres.CreateDatabaseAsync(Ct);

        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddDbContext<TestDbContext>(options => options.UseNpgsql(ConnectionString))
            .AddDurableOutbox<TestDbContext>()
            .BuildServiceProvider();

        await InScopeAsync(scope => scope.GetRequiredService<TestDbContext>().Database.MigrateAsync(Ct));
    }

    public virtual async ValueTask DisposeAsync() => await _services.DisposeAsync();

    /// <summary>A singleton of the container the helpers here use, such as its <see cref="OutboxMetrics"/>.</summary>
    private protected T Service<T>() where T : notnull => _services.GetRequiredService<T>();

    protected async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        await using var scope = _services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }

    protected async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>Adds messages in one committed transaction and returns whether each was queued.</summary>
    protected Task<bool[]> AddAsync(params OutboxMessage[] messages) => InScopeAsync(async scope =>
    {
        var db = scope.GetRequiredService<TestDbContext>();
        var outbox = scope.GetRequiredService<IOutbox>();

        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var queued = new bool[messages.Length];
        for (var i = 0; i < messages.Length; i++)
        {
            queued[i] = await outbox.AddAsync(messages[i], Ct);
        }

        await transaction.CommitAsync(Ct);
        return queued;
    });

    private protected Task<ClaimBatch> ClaimAsync(
        string kind = Kind, int limit = 10, int maxAttempts = 10, TimeSpan? retryWindow = null) =>
        InScopeAsync(scope => Store(scope).ClaimAsync(kind, limit, Lease, maxAttempts, retryWindow, Ct));

    /// <summary>Claims the one message that is due and returns it with its token.</summary>
    private protected async Task<(ClaimedMessage Message, Guid Token)> ClaimOneAsync(int maxAttempts = 10)
    {
        var batch = await ClaimAsync(maxAttempts: maxAttempts);
        return (Assert.Single(batch.Messages), batch.Token);
    }

    private protected Task<bool> CompleteAsync(Guid id, Guid token) =>
        InScopeAsync(scope => Store(scope).CompleteAsync(id, token, Ct));

    /// <summary>Reports a failed attempt, to be retried after <paramref name="delay"/>.</summary>
    private protected Task<FailOutcome> FailAsync(
        Guid id, Guid token, string error, TimeSpan delay = default, int maxAttempts = 10, TimeSpan? retryWindow = null) =>
        InScopeAsync(scope => Store(scope).FailAsync(id, token, error, Time.GetUtcNow() + delay, maxAttempts, retryWindow, Ct));

    private protected Task<bool> RejectAsync(Guid id, Guid token, string reason) =>
        InScopeAsync(scope => Store(scope).RejectAsync(id, token, reason, Ct));

    private protected Task<List<OutboxMessageRow>> RowsAsync() => InScopeAsync(scope =>
        scope.GetRequiredService<TestDbContext>().Set<OutboxMessageRow>().AsNoTracking().ToListAsync(Ct));

    private protected async Task<OutboxMessageRow> RowAsync() => Assert.Single(await RowsAsync());

    private protected async Task<OutboxMessageRow> RowAsync(string recordRef) =>
        Assert.Single(await RowsAsync(), row => row.RecordRef == recordRef);

    private protected Task<List<Guid>> RenewAsync(Guid[] ids, Guid[] tokens) =>
        InScopeAsync(scope => Store(scope).RenewAsync(ids, tokens, Lease, Ct));

    /// <summary>Replaces a message's lease token, as a takeover by another claim would, and returns the new one.</summary>
    protected async Task<Guid> ReplaceTokenAsync(Guid id)
    {
        var token = Guid.NewGuid();
        await InScopeAsync(scope => scope.GetRequiredService<TestDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE svc.outbox SET lease_token = {0} WHERE id = {1}", [token, id], Ct));
        return token;
    }

    /// <summary>Gives a message earlier attempts, so a test can start from a later one.</summary>
    protected Task SeedAttemptsAsync(string recordRef, int attempts) =>
        InScopeAsync(scope => scope.GetRequiredService<TestDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE svc.outbox SET attempts = {0} WHERE record_ref = {1}", [attempts, recordRef], Ct));

    private protected static (int Attempt, string Error)[] Errors(OutboxMessageRow row) =>
        [.. JsonDocument.Parse(row.Errors).RootElement.EnumerateArray()
            .Select(entry => (entry.GetProperty("attempt").GetInt32(), entry.GetProperty("error").GetString()!))];

    private static OutboxStore<TestDbContext> Store(IServiceProvider scope) =>
        scope.GetRequiredService<OutboxStore<TestDbContext>>();
}
