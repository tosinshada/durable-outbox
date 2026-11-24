using Xunit;

namespace DurableOutbox.Tests;

public sealed class OutageTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    // Guarantee 2: the workers carry on after the database has been unreachable.
    [Fact]
    public async Task The_workers_recover_after_the_database_restarts()
    {
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        await IdleAsync();

        await SetDatabaseReachableAsync(false);
        await AdvanceUntilAsync(() => Task.FromResult(FailedClaims() >= 3), Step);
        await SetDatabaseReachableAsync(true);

        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await AdvanceUntilAsync(async () => (await RowAsync()).Status == OutboxStatus.Completed, Step);
        await StopWorkersAsync();

        Assert.Single(Handler.Calls);
    }

    [Fact]
    public async Task A_lease_that_runs_out_without_renewal_cancels_the_handler_and_records_nothing()
    {
        Handler.Behaviour = call => call.UntilCancelledAsync();
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        var call = await Handler.CallAsync(0);
        var claimedAt = Time.GetUtcNow();

        await SetDatabaseReachableAsync(false);
        await AdvanceUntilAsync(call.Cancelled, Step);
        await SetDatabaseReachableAsync(true);
        await call.Returned;
        await StopWorkersAsync();

        // Cancelled at the first renewal tick at or after the lease end.
        Assert.InRange(await call.Cancelled - claimedAt, Lease, Lease + (Lease / 3) + Step);
        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.LeaseToken);
        Assert.Empty(Errors(row));
    }

    private int FailedClaims() =>
        Logs.Entries.Count(entry => entry.Contains("could not be reached during the claim", StringComparison.Ordinal));
}
