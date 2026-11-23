namespace DurableOutbox;

/// <summary>One kind's figures in a gauge sample.</summary>
internal sealed record KindSample(string Kind, long Pending, long Parked, TimeSpan OldestDueAge);
