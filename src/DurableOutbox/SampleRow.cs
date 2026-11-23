namespace DurableOutbox;

/// <summary>One row of the gauge statement: one half's figures for one kind.</summary>
internal sealed class SampleRow
{
    public string Kind { get; init; } = null!;
    public long Pending { get; init; }
    public long Parked { get; init; }
    public DateTimeOffset? OldestDue { get; init; }
}
