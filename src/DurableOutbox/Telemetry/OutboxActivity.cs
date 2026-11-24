using System.Diagnostics;

namespace DurableOutbox;

/// <summary>The <c>DurableOutbox</c> activity source: one consumer activity per attempt.</summary>
internal static class OutboxActivity
{
    public const string SourceName = "DurableOutbox";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Starts <c>outbox.handle {kind}</c> as a child of the writer's trace, or a new trace when the message
    /// carries none. Null when nothing listens.
    /// </summary>
    public static Activity? StartAttempt(OutboxDelivery delivery, string? traceParent)
    {
        // Never a child of whatever happens to be current on the worker: only of the writer.
        Activity.Current = null;
        ActivityContext.TryParse(traceParent, null, out var parent);

        var activity = Source.StartActivity($"outbox.handle {delivery.Kind}", ActivityKind.Consumer, parent);
        activity?
            .SetTag("messaging.system", "durable_outbox")
            .SetTag("messaging.operation.type", "process")
            .SetTag("messaging.destination.name", delivery.Kind)
            .SetTag("messaging.message.id", delivery.Id.ToString())
            .SetTag("durable_outbox.attempt", delivery.Attempt);
        return activity;
    }

    /// <summary>Records how the attempt ended, and stops the activity.</summary>
    public static void Finish(Activity? activity, string? outcome)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("durable_outbox.outcome", outcome);
        if (outcome != AttemptOutcome.Completed)
        {
            activity.SetStatus(ActivityStatusCode.Error, outcome ?? "no outcome was recorded");
        }

        activity.Dispose();
    }
}
