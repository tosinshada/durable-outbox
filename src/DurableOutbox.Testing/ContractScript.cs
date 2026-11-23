namespace DurableOutbox.Testing;

/// <summary>What <see cref="ContractHandler"/> does in one contract call, and every call it received.</summary>
internal sealed class ContractScript
{
    private readonly Lock _gate = new();
    private readonly List<ContractCall> _calls = [];
    private readonly List<(int Index, TaskCompletionSource<ContractCall> Signal)> _waiting = [];

    public Func<ContractCall, Task<OutboxResult>> Behaviour { get; set; } = _ => Task.FromResult(OutboxResult.Done());

    public IReadOnlyList<ContractCall> Calls
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
    public Task<ContractCall> CallAsync(int index)
    {
        lock (_gate)
        {
            if (index < _calls.Count)
            {
                return Task.FromResult(_calls[index]);
            }

            var signal = new TaskCompletionSource<ContractCall>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((index, signal));
            return signal.Task;
        }
    }

    public void Record(ContractCall call)
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
