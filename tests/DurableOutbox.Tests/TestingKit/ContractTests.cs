using DurableOutbox.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

/// <summary>
/// Guarantees 1 to 5 (section 5.3), proved by the test kit's methods: the one copy of these tests, run as a
/// service would run them against its own context.
/// </summary>
public abstract class ContractTests<TContext>(PostgresFixture postgres) : OutboxTestBase(postgres) where TContext : DbContext
{
    [Fact]
    public Task Guarantee_1_written_with_the_change() => RunAsync(contract => contract.WrittenWithTheChangeAsync(Ct));

    [Fact]
    public Task Guarantee_2_at_least_once() => RunAsync(contract => contract.AtLeastOnceAsync(Ct));

    [Fact]
    public Task Guarantee_3_one_owner_at_a_time() => RunAsync(contract => contract.OneOwnerAtATimeAsync(Ct));

    [Fact]
    public Task Guarantee_4_a_crash_is_an_attempt() => RunAsync(contract => contract.CrashIsAnAttemptAsync(Ct));

    [Fact]
    public Task Guarantee_5_never_dropped() => RunAsync(contract => contract.NeverDroppedAsync(Ct));

    /// <summary>Runs one guarantee method against the test's database.</summary>
    protected virtual Task RunAsync(Func<OutboxContract<TContext>, Task> guarantee) =>
        guarantee(new OutboxContract<TContext>(services =>
            services.AddDbContext<TContext>(options => options.UseNpgsql(ConnectionString))));
}
