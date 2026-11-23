using Microsoft.EntityFrameworkCore;

namespace DurableOutbox;

/// <summary>
/// Holds the workers of <typeparamref name="TContext"/> back until the start-up check has passed. Fails
/// when the table does not match, so the workers end without claiming.
/// </summary>
internal sealed class OutboxSchemaGate<TContext> where TContext : DbContext
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the check passed; faults when it found a mismatch.</summary>
    public Task Passed => _settled.Task;

    /// <summary>
    /// Completes while the check is waiting on its clock to try again, so nothing moves until the clock does.
    /// </summary>
    internal Task Waiting
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Task;
            }
        }
    }

    public void Pass() => _settled.TrySetResult();

    public void Fail(Exception mismatch) => _settled.TrySetException(mismatch);

    public void BeginWait()
    {
        lock (_gate)
        {
            _waiting.TrySetResult();
        }
    }

    public void EndWait()
    {
        lock (_gate)
        {
            if (_waiting.Task.IsCompleted)
            {
                _waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
