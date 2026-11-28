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
    /// Reads one message by its ID, in any status from <see cref="OutboxMessageStatus.Pending"/> to
    /// <see cref="OutboxMessageStatus.Cancelled"/>, filled as an entry of <see cref="ListAsync"/> is, with its
    /// most recent 20 errors. Returns null when no message has that ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a plain read by primary key: it takes no lock, opens no transaction and changes nothing. Inside a
    /// transaction open on the same <c>DbContext</c>, it reads what that transaction sees.
    /// </para>
    /// <para>
    /// Use it to say why <see cref="RetryAsync"/> or <see cref="CancelAsync"/> returned false: no such message, or
    /// a message in a status the operation does not apply to. The read and the operation are separate statements,
    /// so the worker or another operator can change the message between them. The boolean those operations return
    /// is still the answer to whether they acted; this read only explains a false.
    /// </para>
    /// </remarks>
    Task<OutboxMessageInfo?> FindAsync(Guid id, CancellationToken cancellationToken);

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
