namespace DurableOutbox;

/// <summary>What recording a failed attempt did.</summary>
internal enum FailOutcome
{
    /// <summary>The lease was lost. Nothing changed.</summary>
    Refused,

    /// <summary>The message waits for its next attempt.</summary>
    Retried,

    /// <summary>The attempts or the retry window were used up.</summary>
    Parked,
}
