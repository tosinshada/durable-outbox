namespace DurableOutbox;

/// <summary>
/// Handles the messages of one kind. Each attempt gets a new instance from its own dependency-injection
/// scope. Handlers must be safe to repeat: a message can be attempted more than once.
/// </summary>
public interface IOutboxHandler
{
    /// <summary>
    /// Handles one attempt. The token is cancelled when the lease is lost, when the attempt runs past its
    /// timeout, and at the end of the drain period on shutdown. An exception counts as a retry.
    /// </summary>
    Task<OutboxResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken);
}
