namespace DurableOutbox;

/// <summary>The checked options, copied when the registration ran. Later changes to the options object have no effect.</summary>
internal sealed record OutboxSettings(
    TimeSpan PollInterval,
    Func<string, string> RedactError,
    TimeSpan DrainTimeout,
    IReadOnlyList<KindSettings> Kinds)
{
    public static OutboxSettings From(OutboxOptions options)
    {
        if (options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.PollInterval), options.PollInterval, "PollInterval must be positive.");
        }

        if (options.DrainTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.DrainTimeout), options.DrainTimeout, "DrainTimeout must not be negative.");
        }

        ArgumentNullException.ThrowIfNull(options.RedactError, nameof(options.RedactError));

        return new OutboxSettings(options.PollInterval, options.RedactError, options.DrainTimeout, [.. options.Kinds]);
    }
}
