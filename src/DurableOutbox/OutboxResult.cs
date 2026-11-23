namespace DurableOutbox;

/// <summary>What a handler reports for an attempt. Only the library creates results.</summary>
public abstract class OutboxResult
{
    private static readonly OutboxResult s_done = new DoneResult();

    private protected OutboxResult()
    {
    }

    /// <summary>The message is done.</summary>
    public static OutboxResult Done() => s_done;

    /// <summary>
    /// The message is done, and <paramref name="record"/> makes follow-up database writes that commit in
    /// one transaction with the completion. It never runs when the lease was lost, and can run more than
    /// once under a retrying execution strategy, so it must only touch the database.
    /// </summary>
    public static OutboxResult Done(Func<CancellationToken, Task> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new DoneWithRecordResult(record);
    }

    /// <summary>The attempt failed. The message is retried after the kind's backoff, or parked when nothing is left.</summary>
    public static OutboxResult Retry(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new RetryResult(error, null);
    }

    /// <summary>The attempt failed. The message is retried at <paramref name="at"/>, which uses an attempt like any retry.</summary>
    public static OutboxResult RetryAt(DateTimeOffset at, string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new RetryResult(error, at);
    }

    /// <summary>The message is refused for good, whatever attempts are left.</summary>
    public static OutboxResult Reject(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new RejectResult(reason);
    }
}
