namespace DurableOutbox;

/// <summary>One row a claim touched. <see cref="Status"/> says what the claim did with it.</summary>
internal sealed class ClaimedMessage
{
    public Guid Id { get; init; }
    public string RecordRef { get; init; } = null!;

    /// <summary>
    /// <c>Pending</c> when it was claimed for handling; <c>Parked</c> or <c>Expired</c> when the claim ended it or,
    /// for a kind with a give-up hook, holds it under the batch's token for the worker to end.
    /// </summary>
    public string Status { get; init; } = null!;

    public int Attempt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public string? TraceParent { get; init; }

    /// <summary>True when the previous attempt never reported: the claim found it still under a lease token.</summary>
    public bool Abandoned { get; init; }
}
