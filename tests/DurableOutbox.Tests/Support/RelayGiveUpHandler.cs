using System.Globalization;

namespace DurableOutbox.Tests;

/// <summary>
/// A handler that never succeeds, registered for two kinds. Its give-up hook queues the next hop, a message of
/// the other kind, until <see cref="Hops"/> messages exist: each kind keeps adding work for the other.
/// The first message's record is <c>hop-1</c>.
/// </summary>
public sealed class RelayGiveUpHandler(IOutbox outbox) : IOutboxHandler, IOutboxGiveUpHandler
{
    public const string FirstKind = "receipt.send";
    public const string SecondKind = "refund.send";
    public const int Hops = 6;

    public Task<OutboxResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken) =>
        Task.FromResult(OutboxResult.Retry("receiver down"));

    public async Task OnGiveUpAsync(OutboxDelivery delivery, OutboxGiveUpReason reason, CancellationToken cancellationToken)
    {
        var hop = int.Parse(delivery.RecordRef.AsSpan("hop-".Length), CultureInfo.InvariantCulture);
        if (hop < Hops)
        {
            var next = delivery.Kind == FirstKind ? SecondKind : FirstKind;
            await outbox.AddAsync(new OutboxMessage(next, $"hop-{hop + 1}"), cancellationToken);
        }
    }
}
