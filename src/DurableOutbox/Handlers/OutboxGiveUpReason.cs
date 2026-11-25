namespace DurableOutbox;

/// <summary>Why the library gave up on a message, passed to <see cref="IOutboxGiveUpHandler"/>.</summary>
public enum OutboxGiveUpReason
{
    /// <summary>The kind's attempt limit is used up. The message becomes <c>Parked</c>.</summary>
    AttemptsUsedUp,

    /// <summary>The kind's retry window has passed, with attempts left. The message becomes <c>Parked</c>.</summary>
    RetryWindowPassed,

    /// <summary>The message's deadline has passed. The message becomes <c>Expired</c>.</summary>
    DeadlinePassed,
}
