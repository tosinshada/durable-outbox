using Xunit;

namespace DurableOutbox.Tests;

public sealed class WorkerLoopTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    [Fact]
    public async Task A_kind_never_runs_more_attempts_than_its_concurrency_limit()
    {
        Handler.Behaviour = call => call.ReleasedAsync();
        await AddAsync([.. Enumerable.Range(1, 5).Select(i => new OutboxMessage(Kind, $"order-{i}"))]);
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind, kind => kind.MaxConcurrency = 2));

        var first = await Handler.CallAsync(0);
        await Handler.CallAsync(1);
        await AdvanceAsync(TimeSpan.FromSeconds(5), Step);
        Assert.Equal(2, Handler.Calls.Count);

        first.Release(OutboxResult.Done());
        await AdvanceUntilAsync(Handler.CallAsync(2), Step);
        await IdleAsync();
        Assert.Equal(3, Handler.Calls.Count);

        Handler.Behaviour = _ => Task.FromResult(OutboxResult.Done());
        foreach (var call in Handler.Calls)
        {
            call.Release(OutboxResult.Done());
        }

        await AdvanceUntilAsync(async () => (await RowsAsync()).All(row => row.Status == OutboxStatus.Completed), Step);
        await StopWorkersAsync();
    }

    [Fact]
    public async Task A_kind_with_every_slot_held_does_not_delay_another_kind()
    {
        Handler.Behaviour = call => call.Delivery.Kind == Kind
            ? call.ReleasedAsync()
            : Task.FromResult(OutboxResult.Done());
        await AddAsync(new OutboxMessage(Kind, "receipt-1"), new OutboxMessage(Kind, "receipt-2"));
        await StartWorkersAsync(outbox =>
        {
            outbox.AddHandler<TestHandler>(Kind, kind => kind.MaxConcurrency = 1);
            outbox.AddHandler<TestHandler>(OtherKind);
        });
        var held = await Handler.CallAsync(0);

        await AddAsync(new OutboxMessage(OtherKind, "invoice-1"), new OutboxMessage("unhandled.kind", "unhandled-1"));
        await AdvanceUntilAsync(async () => (await RowAsync("invoice-1")).Status == OutboxStatus.Completed, Step);

        Assert.False(held.Returned.IsCompleted);
        Assert.Equal(0, (await RowAsync("receipt-2")).Attempts);
        var unhandled = await RowAsync("unhandled-1");
        Assert.Equal(OutboxStatus.Pending, unhandled.Status);
        Assert.Equal(0, unhandled.Attempts);

        Handler.Behaviour = _ => Task.FromResult(OutboxResult.Done());
        held.Release(OutboxResult.Done());
        await StopWorkersAsync();
    }

    [Fact]
    public async Task A_rejected_message_is_finished_by_the_loop()
    {
        Handler.Behaviour = _ => Task.FromResult(OutboxResult.Reject("unknown customer"));
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));

        await Handler.CallAsync(0);
        await StopWorkersAsync();

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Rejected, row.Status);
        Assert.Equal("unknown customer", row.LastError);
        Assert.Single(Handler.Calls);
    }

    [Fact]
    public async Task A_process_that_does_not_run_workers_claims_nothing()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox =>
        {
            outbox.RunWorkers = false;
            outbox.AddHandler<TestHandler>(Kind);
        });

        await AdvanceAsync(TimeSpan.FromMinutes(1), Step);
        await StopWorkersAsync();

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Empty(Handler.Calls);
    }

    [Fact]
    public async Task A_message_past_its_deadline_is_expired_and_never_handled()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1") { Deadline = Time.GetUtcNow() + TimeSpan.FromSeconds(1) });
        Time.Advance(TimeSpan.FromSeconds(2));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));

        await AdvanceUntilAsync(async () => (await RowAsync()).Status == OutboxStatus.Expired, Step);
        await StopWorkersAsync();

        Assert.Empty(Handler.Calls);
        var row = await RowAsync();
        Assert.Contains(Logs.Entries, entry => entry.Contains(row.Id.ToString(), StringComparison.Ordinal)
            && entry.Contains(OutboxStatus.Expired, StringComparison.Ordinal));
    }
}
