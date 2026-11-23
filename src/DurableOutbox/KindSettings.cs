namespace DurableOutbox;

/// <summary>A kind's checked options, copied when the registration ran.</summary>
internal sealed record KindSettings(
    string Kind,
    Type HandlerType,
    TimeSpan Lease,
    int MaxConcurrency,
    TimeSpan? AttemptTimeout,
    int MaxAttempts,
    TimeSpan? RetryWindow,
    TimeSpan BackoffBase,
    TimeSpan BackoffCap)
{
    public static KindSettings From(string kind, Type handlerType, OutboxKindOptions options)
    {
        Positive(kind, nameof(options.Lease), options.Lease);
        AtLeastOne(kind, nameof(options.MaxConcurrency), options.MaxConcurrency);
        if (options.AttemptTimeout is { } timeout)
        {
            Positive(kind, nameof(options.AttemptTimeout), timeout);
        }

        AtLeastOne(kind, nameof(options.MaxAttempts), options.MaxAttempts);
        if (options.RetryWindow is { } window)
        {
            Positive(kind, nameof(options.RetryWindow), window);
        }

        Positive(kind, nameof(options.BackoffBase), options.BackoffBase);
        if (options.BackoffCap < options.BackoffBase)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.BackoffCap), options.BackoffCap, $"Kind '{kind}': BackoffCap must not be less than BackoffBase.");
        }

        return new KindSettings(
            kind, handlerType, options.Lease, options.MaxConcurrency, options.AttemptTimeout, options.MaxAttempts,
            options.RetryWindow, options.BackoffBase, options.BackoffCap);
    }

    private static void Positive(string kind, string option, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(option, value, $"Kind '{kind}': {option} must be positive.");
        }
    }

    private static void AtLeastOne(string kind, string option, int value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(option, value, $"Kind '{kind}': {option} must be at least 1.");
        }
    }
}
