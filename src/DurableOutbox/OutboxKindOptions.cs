namespace DurableOutbox;

/// <summary>The options of one message kind, set in <see cref="OutboxOptions.AddHandler{THandler}"/>.</summary>
public sealed class OutboxKindOptions
{
    /// <summary>How long a claim holds a message. Renewed every third of it while the handler runs.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How many attempts of this kind run at once in one process.</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>How long one attempt may run before its token is cancelled. Null turns the limit off.</summary>
    public TimeSpan? AttemptTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many attempts a message gets before it is parked.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Optional. A message older than this is parked when it next fails or becomes due.</summary>
    public TimeSpan? RetryWindow { get; set; }

    /// <summary>The delay after the first failed attempt. It doubles after each one, up to <see cref="BackoffCap"/>.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest delay between attempts, before jitter.</summary>
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(15);
}
