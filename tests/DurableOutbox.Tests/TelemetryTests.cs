using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace DurableOutbox.Tests;

// Section 9.11: the instruments operators build dashboards and alerts on, and the trace from writer to handler.
public sealed class TelemetryTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    [Fact]
    public async Task Messages_and_attempts_are_counted_by_how_they_ended()
    {
        // An attempt that never reports: claimed here, then left until its lease runs out.
        await AddAsync(new OutboxMessage(Kind, "order-abandoned"));
        Assert.Single((await ClaimAsync()).Messages);

        Handler.Behaviour = call => Task.FromResult(
            call.Delivery is { RecordRef: "order-retry", Attempt: 1 } ? OutboxResult.Retry("down") : OutboxResult.Done());
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        using var metrics = new MetricsCapture(Service<OutboxMetrics>().Meter, WorkerService<OutboxMetrics>().Meter);

        await AddAsync(new OutboxMessage(Kind, "order-retry") { UniqueKey = "order-retry" });
        Assert.False(Assert.Single(await AddAsync(new OutboxMessage(Kind, "order-retry") { UniqueKey = "order-retry" })));
        await AddAsync(new OutboxMessage(Kind, "order-late") { Deadline = Time.GetUtcNow() - TimeSpan.FromSeconds(1) });

        await AdvanceUntilAsync(async () => (await RowsAsync()).All(row => row.Status != OutboxStatus.Pending), Step);
        await StopWorkersAsync();

        // Two inserted after the listener started; the duplicate is not counted.
        Assert.Equal([1.0, 1.0], metrics.Values("durable_outbox.messages.enqueued", ("kind", Kind)));
        Assert.Single(metrics.Values("durable_outbox.attempts", ("kind", Kind), ("outcome", "retry")));
        Assert.Equal(2, metrics.Values("durable_outbox.attempts", ("kind", Kind), ("outcome", "completed")).Length);
        Assert.Single(metrics.Values("durable_outbox.attempts", ("kind", Kind), ("outcome", "abandoned")));
        Assert.Single(metrics.Values("durable_outbox.messages.expired", ("kind", Kind)));
        Assert.Equal(3, metrics.Values("durable_outbox.attempt.duration", ("kind", Kind)).Length);
    }

    [Fact]
    public async Task An_attempt_is_traced_as_a_child_of_the_writers_activity()
    {
        using var writerSource = new ActivitySource("DurableOutbox.Tests.Writer");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is OutboxActivity.SourceName || source == writerSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        Activity? current = null;
        Handler.Behaviour = _ =>
        {
            current = Activity.Current;
            return Task.FromResult(OutboxResult.Done());
        };

        ActivityContext writer;
        using (var writing = writerSource.StartActivity("place order"))
        {
            writer = writing!.Context;
            await AddAsync(new OutboxMessage(Kind, "order-1"));
        }

        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        await AdvanceUntilAsync(Handler.CallAsync(0), Step);
        await StopWorkersAsync();

        var id = (await RowAsync()).Id.ToString();
        var attempt = Assert.Single(stopped, activity =>
            activity.Source.Name == OutboxActivity.SourceName && (string?)activity.GetTagItem("messaging.message.id") == id);
        Assert.Equal($"outbox.handle {Kind}", attempt.OperationName);
        Assert.Equal(ActivityKind.Consumer, attempt.Kind);
        Assert.Equal(writer.TraceId, attempt.TraceId);
        Assert.Equal(writer.SpanId, attempt.ParentSpanId);
        Assert.Same(attempt, current);
        Assert.Equal("completed", attempt.GetTagItem("durable_outbox.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, attempt.Status);
    }
}
