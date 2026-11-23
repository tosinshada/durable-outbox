namespace DurableOutbox;

/// <summary>One row of the operator list, as the statement returns it.</summary>
internal sealed class OutboxListRow
{
    public Guid Id { get; init; }
    public string Kind { get; init; } = null!;
    public string RecordRef { get; init; } = null!;
    public string? UniqueKey { get; init; }
    public int Priority { get; init; }
    public string Status { get; init; } = null!;
    public int Attempts { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? LastError { get; init; }
    public string Errors { get; init; } = null!;
}
