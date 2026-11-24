namespace DurableOutbox;

/// <summary>Where a message is in its life. Every status but <see cref="Pending"/> is final, unless an operator retries it.</summary>
public enum OutboxMessageStatus
{
    /// <summary>Waiting to be attempted, or held by an attempt under a lease.</summary>
    Pending,

    /// <summary>A handler returned done.</summary>
    Completed,

    /// <summary>A handler refused it for good.</summary>
    Rejected,

    /// <summary>Its attempts or its retry window were used up. It waits for an operator.</summary>
    Parked,

    /// <summary>Its deadline passed before it was attempted.</summary>
    Expired,

    /// <summary>An operator cancelled it.</summary>
    Cancelled,
}
