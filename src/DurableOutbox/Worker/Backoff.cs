namespace DurableOutbox;

/// <summary>Delays with jitter: a factor between 0.8 and 1.2, so instances do not move in step.</summary>
internal static class Backoff
{
    private static readonly TimeSpan OutageCap = TimeSpan.FromSeconds(30);

    /// <summary>The delay after failed attempt <paramref name="attempt"/>: <c>min(base × 2^(attempt − 1), cap)</c>, with jitter.</summary>
    public static TimeSpan RetryDelay(int attempt, TimeSpan backoffBase, TimeSpan backoffCap)
    {
        var seconds = Math.Min(backoffBase.TotalSeconds * Math.Pow(2, Math.Max(attempt - 1, 0)), backoffCap.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * Jitter());
    }

    public static TimeSpan Poll(TimeSpan pollInterval) => pollInterval * Jitter();

    /// <summary>The wait after <paramref name="failures"/> claims in a row failed: doubling from the poll interval, up to 30 seconds.</summary>
    public static TimeSpan Outage(int failures, TimeSpan pollInterval)
    {
        var seconds = pollInterval.TotalSeconds * Math.Pow(2, Math.Max(failures - 1, 0)) * Jitter();
        return TimeSpan.FromSeconds(Math.Min(seconds, OutageCap.TotalSeconds));
    }

    private static double Jitter() => 0.8 + (Random.Shared.NextDouble() * 0.4);
}
