using System.ComponentModel.DataAnnotations.Schema;

namespace DurableOutbox;

/// <summary>One row of the operator list, or the row of the operator read by ID, as the statement returns it.</summary>
internal sealed class OutboxListRow
{
    [Column("Id")]
    public Guid Id { get; init; }
    [Column("Kind")]
    public string Kind { get; init; } = null!;
    [Column("RecordRef")]
    public string RecordRef { get; init; } = null!;
    [Column("UniqueKey")]
    public string? UniqueKey { get; init; }
    [Column("Priority")]
    public int Priority { get; init; }
    [Column("Status")]
    public string Status { get; init; } = null!;
    [Column("Attempts")]
    public int Attempts { get; init; }
    [Column("CreatedAt")]
    public DateTimeOffset CreatedAt { get; init; }
    [Column("Deadline")]
    public DateTimeOffset? Deadline { get; init; }
    [Column("FinishedAt")]
    public DateTimeOffset? FinishedAt { get; init; }
    [Column("LastError")]
    public string? LastError { get; init; }
    [Column("Errors")]
    public string Errors { get; init; } = null!;
}
