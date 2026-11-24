namespace DurableOutbox.Tests;

/// <summary>The handler every worker test registers. It does what the test's <see cref="HandlerScript"/> says.</summary>
public sealed class TestHandler(HandlerScript script, TestDbContext db, TimeProvider time) : IOutboxHandler
{
    public async Task<OutboxResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        var call = new HandlerCall(delivery, cancellationToken, this, db, time);
        script.Record(call);
        try
        {
            return await script.Behaviour(call);
        }
        finally
        {
            call.MarkReturned();
        }
    }
}
