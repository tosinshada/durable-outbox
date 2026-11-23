namespace DurableOutbox;

/// <summary>The rows one claim touched, and the lease token every claimed row in it carries.</summary>
internal sealed record ClaimBatch(Guid Token, IReadOnlyList<ClaimedMessage> Messages);
