namespace DurableOutbox;

/// <summary>
/// Optional. A kind's handler class also implements this to be told when the library gives up on a message
/// of that kind: when it parks the message because its attempts or its retry window are used up, or expires
/// it because its deadline has passed. It is not called for a message the handler rejected, or one an
/// operator cancelled.
/// </summary>
public interface IOutboxGiveUpHandler
{
    /// <summary>
    /// Runs inside the transaction that stores the message's new status, on a new instance from a
    /// dependency-injection scope of its own. Changes made through the scope's <c>DbContext</c> are saved
    /// and committed with the status, and <see cref="IOutbox.AddAsync"/> can be called. It never runs when
    /// the lease was lost. It can run more than once for one message, so it must only touch the database
    /// and be safe to repeat. If it throws, its writes are rolled back and the message is parked or expired
    /// without them, with the exception in its error history.
    /// </summary>
    /// <param name="delivery">
    /// The message. <see cref="OutboxDelivery.Attempt"/> is the number of attempts made, which is 0 for a
    /// message that expired before its first attempt.
    /// </param>
    /// <param name="reason">Why the library gave up.</param>
    /// <param name="cancellationToken">Cancelled at the end of the drain period on shutdown.</param>
    Task OnGiveUpAsync(OutboxDelivery delivery, OutboxGiveUpReason reason, CancellationToken cancellationToken);
}
