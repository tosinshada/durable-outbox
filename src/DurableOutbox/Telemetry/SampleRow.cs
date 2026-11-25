using System.ComponentModel.DataAnnotations.Schema;

namespace DurableOutbox;

/// <summary>One row of the gauge statement: one half's figures for one kind.</summary>
internal sealed class SampleRow
{
    [Column("Kind")]
    public string Kind { get; init; } = null!;
    [Column("Pending")]
    public long Pending { get; init; }
    [Column("Parked")]
    public long Parked { get; init; }
    [Column("OldestDue")]
    public DateTimeOffset? OldestDue { get; init; }
}
