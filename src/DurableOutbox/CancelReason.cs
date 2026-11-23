namespace DurableOutbox;

/// <summary>Why an attempt's token was cancelled, which decides what is recorded for it.</summary>
internal enum CancelReason
{
    None,

    /// <summary>Another claim holds the message, or the lease ran out without renewal. Nothing is recorded.</summary>
    LeaseLost,

    /// <summary>The attempt ran past its timeout. A failure, unless the handler returns a result in time.</summary>
    TimedOut,

    /// <summary>The host stopped and the drain period ended. A result the handler returns is recorded; an exception is not.</summary>
    DrainEnded,
}
