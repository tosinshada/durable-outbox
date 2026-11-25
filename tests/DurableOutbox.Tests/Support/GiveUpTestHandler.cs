namespace DurableOutbox.Tests;

/// <summary>
/// <see cref="TestHandler"/> with a give-up hook. The hook records its call in the test's
/// <see cref="HandlerScript"/> and then does what the script's <see cref="HandlerScript.OnGiveUp"/> says.
/// </summary>
public sealed class GiveUpTestHandler(HandlerScript script, TestDbContext db, TimeProvider time) : IOutboxHandler, IOutboxGiveUpHandler
{
    public Task<OutboxResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken) =>
        new TestHandler(script, db, time).HandleAsync(delivery, cancellationToken);

    public Task OnGiveUpAsync(OutboxDelivery delivery, OutboxGiveUpReason reason, CancellationToken cancellationToken)
    {
        script.RecordGiveUp(delivery, reason);
        return script.OnGiveUp(db, delivery, cancellationToken);
    }
}
