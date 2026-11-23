namespace DurableOutbox;

internal sealed class RejectResult(string reason) : OutboxResult
{
    public string Reason { get; } = reason;
}
