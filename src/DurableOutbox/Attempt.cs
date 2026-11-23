using System.Diagnostics;

namespace DurableOutbox;

/// <summary>
/// One claimed message while its handler runs. <see cref="State"/>, <see cref="Reason"/> and
/// <see cref="LeaseEnd"/> are guarded by the loop that owns the attempt.
/// </summary>
internal sealed class Attempt(OutboxDelivery delivery, Guid token, DateTimeOffset leaseEnd) : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _timedOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _timeout;

    public OutboxDelivery Delivery { get; } = delivery;
    public Guid Id => Delivery.Id;
    public Guid Token { get; } = token;
    public DateTimeOffset LeaseEnd { get; set; } = leaseEnd;
    public AttemptState State { get; set; }
    public CancelReason Reason { get; set; }

    /// <summary>The writer's trace context, the parent of the attempt's activity.</summary>
    public string? TraceParent { get; init; }

    /// <summary>The attempt's activity, current while its handler runs. Null when nothing listens.</summary>
    public Activity? Activity { get; set; }

    /// <summary>When the handler started, from <see cref="TimeProvider.GetTimestamp"/>.</summary>
    public long StartedAt { get; set; }

    /// <summary>How the attempt ended, once that is known: an <see cref="AttemptOutcome"/> value, or null when nothing was recorded.</summary>
    public string? Outcome { get; set; }

    /// <summary>The handler's token.</summary>
    public CancellationToken HandlerToken => _cancel.Token;

    /// <summary>Completes when the attempt's token was cancelled for its timeout.</summary>
    public Task TimedOut => _timedOut.Task;

    public void StartTimeout(TimeSpan timeout, TimeProvider time, Func<bool> cancelForTimeout)
    {
        _timeout = new CancellationTokenSource(timeout, time);
        _timeout.Token.Register(() =>
        {
            if (cancelForTimeout())
            {
                _timedOut.TrySetResult();
            }
        });
    }

    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt ended while it was being cancelled.
        }
    }

    public void Dispose()
    {
        _timeout?.Dispose();
        _cancel.Dispose();
    }
}
