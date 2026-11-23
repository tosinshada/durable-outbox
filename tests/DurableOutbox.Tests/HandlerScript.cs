namespace DurableOutbox.Tests;

/// <summary>What <see cref="TestHandler"/> does, set by the test, and every call it received.</summary>
public sealed class HandlerScript
{
    private readonly Lock _gate = new();
    private readonly List<HandlerCall> _calls = [];
    private readonly List<(int Index, TaskCompletionSource<HandlerCall> Signal)> _waiting = [];

    public Func<HandlerCall, Task<OutboxResult>> Behaviour { get; set; } = _ => Task.FromResult(OutboxResult.Done());

    public IReadOnlyList<HandlerCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Completes when call number <paramref name="index"/> (from 0) has started.</summary>
    public Task<HandlerCall> CallAsync(int index)
    {
        lock (_gate)
        {
            if (index < _calls.Count)
            {
                return Task.FromResult(_calls[index]);
            }

            var signal = new TaskCompletionSource<HandlerCall>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((index, signal));
            return signal.Task;
        }
    }

    internal void Record(HandlerCall call)
    {
        lock (_gate)
        {
            _calls.Add(call);
            foreach (var (index, signal) in _waiting.Where(waiter => waiter.Index == _calls.Count - 1))
            {
                signal.TrySetResult(call);
            }
        }
    }
}
