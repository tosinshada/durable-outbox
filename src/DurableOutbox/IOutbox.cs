namespace DurableOutbox;

/// <summary>Writes messages to the outbox inside the caller's open transaction.</summary>
public interface IOutbox
{
    /// <summary>
    /// Adds a message at once, in the transaction the <c>DbContext</c> has open.
    /// Returns false when a message with the same kind and unique key already exists.
    /// </summary>
    /// <exception cref="InvalidOperationException">The <c>DbContext</c> has no open transaction.</exception>
    Task<bool> AddAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
