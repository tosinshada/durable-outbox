using Xunit;

namespace DurableOutbox.Tests;

public sealed class ShutdownTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Shutdown_stops_claiming_and_records_the_outcome_of_a_handler_that_finishes()
    {
        Handler.Behaviour = call => call.ReleasedAsync();
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        var call = await Handler.CallAsync(0);

        var stopping = StopWorkersAsync();
        await AddAsync(new OutboxMessage(Kind, "order-2"));
        await AdvanceAsync(TimeSpan.FromSeconds(5), Step);
        call.Release(OutboxResult.Done());
        await stopping;

        Assert.Equal(OutboxStatus.Completed, (await RowAsync("order-1")).Status);
        Assert.Equal(0, (await RowAsync("order-2")).Attempts);
        Assert.Single(Handler.Calls);
    }

    // Guarantee 4: an attempt cut off by shutdown records nothing, and its message comes back after the lease.
    [Fact]
    public async Task Shutdown_cancels_a_handler_still_running_when_the_drain_period_ends()
    {
        Handler.Behaviour = call => call.UntilCancelledAsync();
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        var call = await Handler.CallAsync(0);

        var stoppedAt = Time.GetUtcNow();
        var stopping = StopWorkersAsync();

        // Only the end of the drain period waits on the clock. The handler returning and the stop finishing
        // after that do not, so they are awaited rather than stepped through.
        await AdvanceUntilAsync(call.Cancelled, Step);
        await stopping;

        Assert.InRange(await call.Cancelled - stoppedAt, DrainTimeout, DrainTimeout + Step);
        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Empty(Errors(row));

        Time.Advance(Lease);
        var (again, _) = await ClaimOneAsync();
        Assert.Equal(2, again.Attempt);
    }
}
