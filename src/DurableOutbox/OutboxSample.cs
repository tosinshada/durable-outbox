namespace DurableOutbox;

/// <summary>
/// What the gauges and the health check read. <see cref="Kinds"/> are the figures of the last sample that
/// succeeded; <see cref="Failed"/> is true when the most recent one could not be taken.
/// </summary>
internal sealed record OutboxSample(DateTimeOffset TakenAt, IReadOnlyList<KindSample> Kinds, bool Failed);
