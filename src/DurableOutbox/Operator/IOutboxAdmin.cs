namespace DurableOutbox;

/// <summary>
/// What an operator needs to see and fix stuck messages. The library adds no endpoints: the service
/// decides who may call these.
/// </summary>
public interface IOutboxAdmin
{
    /// <summary>
    /// Lists <see cref="OutboxMessageStatus.Parked"/> or <see cref="OutboxMessageStatus.Rejected"/> messages,
    /// newest finished first. Pass the last entry of a page as <see cref="OutboxQuery.After"/> to get the next.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The status is neither <c>Parked</c> nor <c>Rejected</c>, the limit is outside 1 to 1,000, or
    /// <see cref="OutboxQuery.After"/> has no finish time.
    /// </exception>
    Task<IReadOnlyList<OutboxMessageInfo>> ListAsync(OutboxQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Makes a <c>Parked</c> or <c>Rejected</c> message due at once, with its attempts reset and its errors kept.
    /// Returns false when the message does not exist or is in another status.
    /// </summary>
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a <c>Pending</c> or <c>Parked</c> message for good. An attempt running when it is cancelled
    /// has its outcome refused, and its handler is cancelled at the next lease renewal. Returns false when
    /// the message does not exist or is in another status.
    /// </summary>
    Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken);
}
