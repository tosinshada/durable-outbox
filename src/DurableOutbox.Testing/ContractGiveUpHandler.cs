namespace DurableOutbox.Testing;

/// <summary>
/// The handler the contract registers for its kind with a give-up hook. It handles as <see cref="ContractHandler"/>
/// does. Its hook queues a message of the script's follow-up kind, a write that must commit with the status, and
/// then throws when the script says so.
/// </summary>
internal sealed class ContractGiveUpHandler(ContractScript script, IOutbox outbox) : IOutboxHandler, IOutboxGiveUpHandler
{
    public const string HookError = "the service's record could not be marked";

    public Task<OutboxResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken) =>
        new ContractHandler(script).HandleAsync(delivery, cancellationToken);

    public async Task OnGiveUpAsync(OutboxDelivery delivery, OutboxGiveUpReason reason, CancellationToken cancellationToken)
    {
        await outbox.AddAsync(
            new OutboxMessage(script.FollowUpKind, $"{delivery.RecordRef}:{reason}:{delivery.Attempt}"), cancellationToken);
        if (script.HookThrows)
        {
            throw new InvalidOperationException(HookError);
        }
    }
}
