using Xunit;

namespace DurableOutbox.Tests;

public sealed class LeaseTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    // Guarantee 3: a renewed lease is not taken over, whichever claim the attempt came from.
    [Fact]
    public async Task A_renewed_lease_is_not_taken_over()
    {
        Handler.Behaviour = call => call.ReleasedAsync();
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind, kind => kind.MaxConcurrency = 2));
        await Handler.CallAsync(0);

        // A second claim, ten seconds later, so the two attempts carry different tokens.
        await AdvanceAsync(TimeSpan.FromSeconds(10), Step);
        await AddAsync(new OutboxMessage(Kind, "order-2"));
        await AdvanceUntilAsync(Handler.CallAsync(1), Step);

        for (var elapsed = 0; elapsed < 150; elapsed += 20)
        {
            await AdvanceAsync(TimeSpan.FromSeconds(20), Step);
            Assert.Empty((await ClaimAsync()).Messages);
        }

        foreach (var call in Handler.Calls)
        {
            call.Release(OutboxResult.Done());
        }

        await StopWorkersAsync();

        Assert.Equal(2, Handler.Calls.Count);
        Assert.All(await RowsAsync(), row =>
        {
            Assert.Equal(OutboxStatus.Completed, row.Status);
            Assert.Equal(1, row.Attempts);
        });
    }

    // Guarantee 3: a handler whose claim was taken over is stopped, and its result changes nothing.
    [Fact]
    public async Task A_handler_whose_lease_is_lost_is_cancelled_and_its_result_is_not_recorded()
    {
        Handler.Behaviour = call => call.ReleasedAsync();
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind, kind =>
        {
            kind.MaxConcurrency = 1;
            kind.AttemptTimeout = null;
        }));
        var call = await Handler.CallAsync(0);
        var claimedAt = Time.GetUtcNow();

        var takenOverBy = await ReplaceTokenAsync(call.Delivery.Id);
        await AdvanceUntilAsync(call.Cancelled, Step);

        // At the first renewal, a third of the lease after the claim.
        Assert.InRange(await call.Cancelled - claimedAt, TimeSpan.Zero, (Lease / 3) + Step);
        call.Release(OutboxResult.Done());
        await call.Returned;
        await StopWorkersAsync();

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(takenOverBy, row.LeaseToken);
        Assert.Empty(Errors(row));
        Assert.Contains(Logs.Entries, entry => entry.Contains("lost its lease", StringComparison.Ordinal));
    }
}
