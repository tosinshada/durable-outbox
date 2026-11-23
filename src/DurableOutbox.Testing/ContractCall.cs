namespace DurableOutbox.Testing;

/// <summary>One call to <see cref="ContractHandler"/>: what it was given, and the means to finish it.</summary>
internal sealed class ContractCall(OutboxDelivery delivery, ContractHandler handler)
{
    private readonly TaskCompletionSource<OutboxResult> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OutboxDelivery Delivery { get; } = delivery;

    /// <summary>The handler instance, one per attempt scope.</summary>
    public ContractHandler Handler { get; } = handler;

    public void Release(OutboxResult result) => _release.TrySetResult(result);

    /// <summary>Waits for <see cref="Release"/>.</summary>
    public Task<OutboxResult> ReleasedAsync() => _release.Task;
}
