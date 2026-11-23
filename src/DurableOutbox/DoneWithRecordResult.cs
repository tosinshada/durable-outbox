namespace DurableOutbox;

internal sealed class DoneWithRecordResult(Func<CancellationToken, Task> record) : OutboxResult
{
    public Func<CancellationToken, Task> Record { get; } = record;
}
