namespace DurableOutbox;

/// <summary>Which messages <see cref="IOutboxAdmin.ListAsync"/> returns.</summary>
/// <param name="Status"><see cref="OutboxMessageStatus.Parked"/> or <see cref="OutboxMessageStatus.Rejected"/>.</param>
public sealed record OutboxQuery(OutboxMessageStatus Status)
{
    /// <summary>Only messages of this kind. Null lists every kind.</summary>
    public string? Kind { get; init; }

    /// <summary>The most entries to return, from 1 to 1,000. Default 50.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>The last entry of the previous page. The list continues after it. Null starts at the newest.</summary>
    public OutboxMessageInfo? After { get; init; }
}
