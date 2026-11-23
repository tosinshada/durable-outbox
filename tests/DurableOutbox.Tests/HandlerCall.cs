namespace DurableOutbox.Tests;

/// <summary>One call to <see cref="TestHandler"/>: what it was given, and the means to finish it from the test.</summary>
public sealed class HandlerCall
{
    private readonly TaskCompletionSource<DateTimeOffset> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<OutboxResult> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HandlerCall(OutboxDelivery delivery, CancellationToken token, TestHandler handler, TestDbContext db, TimeProvider time)
    {
        Delivery = delivery;
        Token = token;
        Handler = handler;
        Db = db;
        token.Register(() => _cancelled.TrySetResult(time.GetUtcNow()));
    }

    public OutboxDelivery Delivery { get; }
    public CancellationToken Token { get; }

    /// <summary>The handler instance, one per scope.</summary>
    public TestHandler Handler { get; }

    /// <summary>The attempt scope's context.</summary>
    public TestDbContext Db { get; }

    /// <summary>Completes with the clock's time when the handler's token is cancelled.</summary>
    public Task<DateTimeOffset> Cancelled => _cancelled.Task;

    /// <summary>Completes when the handler has returned or thrown.</summary>
    public Task Returned => _returned.Task;

    public void Release(OutboxResult result) => _release.TrySetResult(result);

    /// <summary>Waits for <see cref="Release"/> and ignores the token, as a handler that does not honour it would.</summary>
    public Task<OutboxResult> ReleasedAsync() => _release.Task;

    /// <summary>Waits on the token only, and throws when it is cancelled.</summary>
    public async Task<OutboxResult> UntilCancelledAsync()
    {
        await Task.Delay(Timeout.Infinite, Token);
        throw new InvalidOperationException("Unreachable.");
    }

    internal void MarkReturned() => _returned.TrySetResult();
}
