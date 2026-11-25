using System.ComponentModel.DataAnnotations.Schema;

namespace DurableOutbox;

/// <summary>One row a claim touched. <see cref="Status"/> says what the claim did with it.</summary>
internal sealed class ClaimedMessage
{
    [Column("Id")]
    public Guid Id { get; init; }
    [Column("RecordRef")]
    public string RecordRef { get; init; } = null!;

    /// <summary>
    /// <c>Pending</c> when it was claimed for handling; <c>Parked</c> or <c>Expired</c> when the claim ended it or,
    /// for a kind with a give-up hook, holds it under the batch's token for the worker to end.
    /// </summary>
    [Column("Status")]
    public string Status { get; init; } = null!;

    [Column("Attempt")]
    public int Attempt { get; init; }
    [Column("CreatedAt")]
    public DateTimeOffset CreatedAt { get; init; }
    [Column("Deadline")]
    public DateTimeOffset? Deadline { get; init; }
    [Column("TraceParent")]
    public string? TraceParent { get; init; }

    /// <summary>True when the previous attempt never reported: the claim found it still under a lease token.</summary>
    [Column("Abandoned")]
    public bool Abandoned { get; init; }
}
