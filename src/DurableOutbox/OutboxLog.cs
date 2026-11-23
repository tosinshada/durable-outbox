using Microsoft.Extensions.Logging;

namespace DurableOutbox;

/// <summary>The workers' log messages. Error text in them has always passed through the service's filter.</summary>
internal static partial class OutboxLog
{
    [LoggerMessage(1, LogLevel.Warning,
        "Outbox kind {Kind}: the database could not be reached during the {Operation}; trying again in {RetryIn}.")]
    public static partial void DatabaseUnreachable(
        ILogger logger, string kind, string operation, TimeSpan retryIn, Exception exception);

    [LoggerMessage(2, LogLevel.Warning,
        "Outbox message {MessageId} ({Kind}, attempt {Attempt}) lost its lease. Its handler was cancelled and its result will not be recorded.")]
    public static partial void LeaseLost(ILogger logger, Guid messageId, string kind, int attempt);

    [LoggerMessage(3, LogLevel.Warning, "Outbox message {MessageId} ({Kind}, attempt {Attempt}) failed: {Error}")]
    public static partial void HandlerFailed(ILogger logger, Guid messageId, string kind, int attempt, string error);

    [LoggerMessage(4, LogLevel.Warning, "Outbox message {MessageId} ({Kind}, attempt {Attempt}) timed out.")]
    public static partial void AttemptTimedOut(ILogger logger, Guid messageId, string kind, int attempt);

    [LoggerMessage(5, LogLevel.Warning,
        "Outbox message {MessageId} ({Kind}, record {RecordRef}) is {Status}. It was ended by the claim and will not be handled.")]
    public static partial void EndedByClaim(ILogger logger, Guid messageId, string kind, string recordRef, string status);

    [LoggerMessage(6, LogLevel.Error,
        "Outbox message {MessageId} ({Kind}, attempt {Attempt}): its outcome could not be written. It is attempted again after its lease.")]
    public static partial void OutcomeWriteFailed(ILogger logger, Guid messageId, string kind, int attempt, Exception exception);

    [LoggerMessage(7, LogLevel.Information,
        "Outbox message {MessageId} ({Kind}, attempt {Attempt}): its outcome was refused because the lease was lost.")]
    public static partial void OutcomeRefused(ILogger logger, Guid messageId, string kind, int attempt);
}
