using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

public sealed class LeaseTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
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

    // Guarantee 3 (9.10): an operator cancel fences out the attempt that holds the message.
    [Fact]
    public async Task Cancelling_a_message_mid_attempt_stops_its_handler_and_refuses_its_outcome()
    {
        Handler.Behaviour = call => call.ReleasedAsync();
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind, kind => kind.AttemptTimeout = null));
        var call = await Handler.CallAsync(0);
        var claimedAt = Time.GetUtcNow();

        Assert.True(await InScopeAsync(scope =>
            scope.GetRequiredService<IOutboxAdmin>().CancelAsync(call.Delivery.Id, Ct)));
        await AdvanceUntilAsync(call.Cancelled, Step);

        Assert.InRange(await call.Cancelled - claimedAt, TimeSpan.Zero, (Lease / 3) + Step);
        call.Release(OutboxResult.Done());
        await call.Returned;
        await StopWorkersAsync();

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Cancelled, row.Status);
        Assert.Null(row.LeaseToken);
    }
}
