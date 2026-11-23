namespace DurableOutbox;

/// <summary>A reference to a record and the kind of work to do on it. It carries no body.</summary>
/// <param name="Kind">Which handler runs.</param>
/// <param name="RecordRef">The identifier of the record the handler acts on.</param>
public sealed record OutboxMessage(string Kind, string RecordRef)
{
    /// <summary>Higher is claimed first. Default 0.</summary>
    public int Priority { get; init; }

    /// <summary>A message not done by then expires instead of being processed late.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>A second message with the same kind and key is not queued.</summary>
    public string? UniqueKey { get; init; }
}
