namespace DurableOutbox;

/// <summary>A failed attempt. <see cref="At"/> is null when the kind's backoff decides the next due time.</summary>
internal sealed class RetryResult(string error, DateTimeOffset? at) : OutboxResult
{
    public string Error { get; } = error;
    public DateTimeOffset? At { get; } = at;
}
