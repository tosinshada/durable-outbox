namespace DurableOutbox;

/// <summary>The <c>outcome</c> tag of <c>durable_outbox.attempts</c>: how an attempt ended.</summary>
internal static class AttemptOutcome
{
    public const string Completed = "completed";
    public const string Retry = "retry";
    public const string TimedOut = "timed_out";
    public const string Rejected = "rejected";
    public const string Parked = "parked";
    public const string LeaseLost = "lease_lost";
    public const string Abandoned = "abandoned";
}
