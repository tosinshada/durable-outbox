using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

// Section 9.5, the give-up hook. Its writes committing with the status on each path, and a hook that throws,
// are the test kit's GiveUpHookAsync (ContractTests). These are the cases that need a second actor or a metric.
public sealed class GiveUpHookTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    // Guarantee 3: the end of a held message is fenced. An operator's cancel between the hold and the end wins.
    [Fact]
    public async Task A_held_message_cancelled_before_its_end_is_not_ended()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1") { Deadline = Time.GetUtcNow() + TimeSpan.FromSeconds(1) });
        Time.Advance(TimeSpan.FromSeconds(2));

        var held = await ClaimAsync(hold: true);
        var message = Assert.Single(held.Messages);
        Assert.Equal(OutboxStatus.Expired, message.Status);
        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(held.Token, row.LeaseToken);
        Assert.Equal(0, row.Attempts);

        Assert.True(await InScopeAsync(scope => scope.GetRequiredService<IOutboxAdmin>().CancelAsync(message.Id, Ct)));
        Assert.False(await InScopeAsync(scope => scope.GetRequiredService<OutboxStore<TestDbContext>>()
            .EndAsync(message.Id, held.Token, OutboxStatus.Expired, error: null, Ct)));

        Assert.Equal(OutboxStatus.Cancelled, (await RowAsync()).Status);
    }

    // Guarantee 3: a worker whose claim was taken over writes nothing, so its hook never runs.
    [Fact]
    public async Task The_hook_never_runs_when_the_last_attempt_lost_its_lease()
    {
        Handler.Behaviour = call => call.ReleasedAsync();
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<GiveUpTestHandler>(Kind, kind => kind.MaxAttempts = 1));
        var call = await Handler.CallAsync(0);

        var takenOverBy = await ReplaceTokenAsync(call.Delivery.Id);
        call.Release(OutboxResult.Retry("down"));
        await StopWorkersAsync();

        Assert.Empty(Handler.GiveUps);
        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(takenOverBy, row.LeaseToken);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task A_message_past_its_retry_window_is_given_up_for_that_reason()
    {
        Handler.Behaviour = _ => Task.FromResult(OutboxResult.Retry("down"));
        Handler.OnGiveUp = (db, delivery, _) =>
        {
            db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = delivery.RecordRef });
            return Task.CompletedTask;
        };
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        Time.Advance(TimeSpan.FromHours(2));
        await StartWorkersAsync(outbox => outbox.AddHandler<GiveUpTestHandler>(Kind, kind => kind.RetryWindow = TimeSpan.FromHours(1)));

        await AdvanceUntilAsync(async () => (await RowAsync()).Status == OutboxStatus.Parked, Step);
        await StopWorkersAsync();

        var (delivery, reason) = Assert.Single(Handler.GiveUps);
        Assert.Equal(OutboxGiveUpReason.RetryWindowPassed, reason);
        Assert.Equal(1, delivery.Attempt);
        Assert.Equal([(1, "down")], Errors(await RowAsync()));

        // Changes the hook left tracked in its scope's context are saved with the status.
        Assert.Equal(["order-1"], await InScopeAsync(scope =>
            scope.GetRequiredService<TestDbContext>().Orders.AsNoTracking().Select(order => order.Reference).ToListAsync(Ct)));
    }

    // Section 9.9: a held message uses a slot until its end is written, so hooks respect the concurrency limit.
    [Fact]
    public async Task Hooks_never_run_beyond_the_concurrency_limit()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        Handler.OnGiveUp = async (_, _, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            if (now == 2)
            {
                two.TrySetResult();
            }

            await release.Task;
            Interlocked.Decrement(ref running);
        };
        var deadline = Time.GetUtcNow() + TimeSpan.FromSeconds(1);
        await AddAsync([.. Enumerable.Range(1, 5).Select(i => new OutboxMessage(Kind, $"order-{i}") { Deadline = deadline })]);
        Time.Advance(TimeSpan.FromSeconds(2));
        await StartWorkersAsync(outbox => outbox.AddHandler<GiveUpTestHandler>(Kind, kind => kind.MaxConcurrency = 2));

        // Two hooks hold both slots. Moving the clock past many polls starts no third.
        await two.Task.WaitAsync(Ct);
        Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, Handler.GiveUps.Count);

        release.SetResult();
        await AdvanceUntilAsync(async () => (await RowsAsync()).All(row => row.Status == OutboxStatus.Expired), Step);
        await StopWorkersAsync();

        Assert.Equal(5, Handler.GiveUps.Count);
        Assert.Equal(2, most);
        Assert.Empty(Handler.Calls);
    }

    // Section 9.11: a failed hook is counted, and the attempt it followed is still counted as parked.
    [Fact]
    public async Task A_hook_that_throws_is_counted_and_logged()
    {
        Handler.Behaviour = _ => Task.FromResult(OutboxResult.Retry("down"));
        Handler.OnGiveUp = (_, _, _) => throw new InvalidOperationException("ledger closed");
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<GiveUpTestHandler>(Kind, kind => kind.MaxAttempts = 1));
        using var metrics = new MetricsCapture(WorkerService<OutboxMetrics>().Meter);

        await AdvanceUntilAsync(async () => (await RowAsync()).Status == OutboxStatus.Parked, Step);
        await StopWorkersAsync();

        Assert.Single(metrics.Values("durable_outbox.give_up_hook.failures", ("kind", Kind)));
        Assert.Single(metrics.Values("durable_outbox.attempts", ("kind", Kind), ("outcome", "parked")));
        var row = await RowAsync();
        Assert.Contains(Logs.Entries, entry => entry.Contains(row.Id.ToString(), StringComparison.Ordinal)
            && entry.Contains("ledger closed", StringComparison.Ordinal));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
