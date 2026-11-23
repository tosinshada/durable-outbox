namespace DurableOutbox;

/// <summary>A message as an operator sees it.</summary>
/// <param name="Id">The message ID, the same one handlers receive.</param>
/// <param name="Kind">Which handler runs.</param>
/// <param name="RecordRef">The record the handler acts on.</param>
/// <param name="UniqueKey">The uniqueness key, if it has one.</param>
/// <param name="Priority">Higher is claimed first.</param>
/// <param name="Status">Where the message is in its life.</param>
/// <param name="Attempts">Attempts made since it was written or last retried by an operator.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="Deadline">When it expires if not done, if it has a deadline.</param>
/// <param name="FinishedAt">When it reached its current final status. Null while pending.</param>
/// <param name="LastError">The most recent error, after the service's filter.</param>
/// <param name="Errors">The most recent 20 errors, oldest first.</param>
public sealed record OutboxMessageInfo(
    Guid Id,
    string Kind,
    string RecordRef,
    string? UniqueKey,
    int Priority,
    OutboxMessageStatus Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? Deadline,
    DateTimeOffset? FinishedAt,
    string? LastError,
    IReadOnlyList<OutboxAttemptError> Errors);
