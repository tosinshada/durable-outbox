namespace DurableOutbox.Testing;

/// <summary>The handler the contract registers for its own kinds. It does what the call's script says.</summary>
internal sealed class ContractHandler(ContractScript script) : IOutboxHandler
{
    public Task<OutboxResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        var call = new ContractCall(delivery, this);
        script.Record(call);
        return script.Behaviour(call);
    }
}
