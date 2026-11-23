using Xunit;

namespace DurableOutbox.Tests;

public sealed class TimeoutTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task An_attempt_past_its_timeout_is_cancelled_and_retried()
    {
        Handler.Behaviour = call => call.Delivery.Attempt == 1
            ? call.UntilCancelledAsync()
            : Task.FromResult(OutboxResult.Done());
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind, kind => kind.AttemptTimeout = AttemptTimeout));
        var first = await Handler.CallAsync(0);
        var started = Time.GetUtcNow();

        await AdvanceUntilAsync(Handler.CallAsync(1), Step);
        await StopWorkersAsync();

        var cancelledAt = await first.Cancelled;
        Assert.InRange(cancelledAt - started, AttemptTimeout, AttemptTimeout + Step);
        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Completed, row.Status);
        Assert.Equal([(1, "timed out")], Errors(row));
    }

    // A hung handler cannot hold a slot: the slot is freed after the grace period, and its late result is refused.
    [Fact]
    public async Task A_handler_that_ignores_its_timeout_gives_up_its_slot_and_its_late_result_is_refused()
    {
        Handler.Behaviour = call => call.Delivery.RecordRef == "order-1"
            ? call.ReleasedAsync()
            : Task.FromResult(OutboxResult.Done());
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        Time.Advance(Step);
        await AddAsync(new OutboxMessage(Kind, "order-2"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind, kind =>
        {
            kind.MaxConcurrency = 1;
            kind.AttemptTimeout = AttemptTimeout;
        }));
        var hung = await Handler.CallAsync(0);
        var started = Time.GetUtcNow();

        var next = await AdvanceAndWaitAsync(Handler.CallAsync(1));

        Assert.Equal("order-2", next.Delivery.RecordRef);
        Assert.True(Time.GetUtcNow() - started >= AttemptTimeout + TimeSpan.FromSeconds(5));
        Assert.False(hung.Returned.IsCompleted);

        hung.Release(OutboxResult.Done());
        await hung.Returned;
        await IdleAsync();

        var row = await RowAsync("order-1");
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Null(row.LeaseToken);
        Assert.Equal("timed out", row.LastError);
        await StopWorkersAsync();
    }

    private async Task<HandlerCall> AdvanceAndWaitAsync(Task<HandlerCall> call)
    {
        await AdvanceUntilAsync(call, Step);
        return await call;
    }
}
