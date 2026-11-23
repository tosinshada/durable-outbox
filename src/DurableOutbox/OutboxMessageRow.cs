namespace DurableOutbox;

/// <summary>
/// Maps the outbox table so the service's migrations create it. The library never tracks it:
/// every read and write is raw SQL.
/// </summary>
internal sealed class OutboxMessageRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = null!;
    public string RecordRef { get; set; } = null!;
    public string? UniqueKey { get; set; }
    public int Priority { get; set; }
    public string Status { get; set; } = null!;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? LastError { get; set; }
    public string Errors { get; set; } = null!;
    public string? TraceParent { get; set; }
}
