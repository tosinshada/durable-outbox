namespace DurableOutbox;

/// <summary>The error of one attempt, as stored after the service's filter.</summary>
/// <param name="Attempt">The attempt number it belongs to.</param>
/// <param name="At">When it was recorded.</param>
/// <param name="Error">The error text.</param>
public sealed record OutboxAttemptError(int Attempt, DateTimeOffset At, string Error);
