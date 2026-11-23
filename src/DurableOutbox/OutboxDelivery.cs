namespace DurableOutbox;

/// <summary>
/// One attempt at a message. <see cref="Id"/> is the same on every attempt, so a receiver can recognise
/// a repeat.
/// </summary>
public sealed record OutboxDelivery(
    Guid Id,
    string Kind,
    string RecordRef,
    int Attempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? Deadline);
