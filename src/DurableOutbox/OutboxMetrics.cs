using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DurableOutbox;

/// <summary>
/// The <c>DurableOutbox</c> meter and its instruments, one per container. The gauges read the latest
/// sample of each registered sampler and never query.
/// </summary>
internal sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "DurableOutbox";

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _enqueued;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _expired;
    private readonly Histogram<double> _duration;
    private readonly Lock _gate = new();
    private readonly List<Func<OutboxSample?>> _samples = [];

    public OutboxMetrics(IMeterFactory? meters)
    {
        _ownsMeter = meters is null;
        _meter = meters?.Create(new MeterOptions(MeterName)) ?? new Meter(MeterName);

        _enqueued = _meter.CreateCounter<long>(
            "durable_outbox.messages.enqueued", description: "Messages added to the outbox.");
        _attempts = _meter.CreateCounter<long>(
            "durable_outbox.attempts", description: "Attempts, by how they ended.");
        _expired = _meter.CreateCounter<long>(
            "durable_outbox.messages.expired", description: "Messages whose deadline passed before they were attempted.");
        _duration = _meter.CreateHistogram<double>(
            "durable_outbox.attempt.duration", unit: "s", description: "How long an attempt took, from its handler's start to its outcome.");

        _meter.CreateObservableGauge(
            "durable_outbox.messages.pending", () => Observe(kind => kind.Pending), description: "Pending messages, as last sampled.");
        _meter.CreateObservableGauge(
            "durable_outbox.messages.parked", () => Observe(kind => kind.Parked), description: "Parked messages, as last sampled.");
        _meter.CreateObservableGauge(
            "durable_outbox.oldest_due.age", () => Observe(kind => kind.OldestDueAge.TotalSeconds), unit: "s",
            description: "How long the earliest due pending message has waited, as last sampled.");
    }

    /// <summary>The meter, so a test can listen to this container's instruments only.</summary>
    internal Meter Meter => _meter;

    public void Enqueued(string kind) => _enqueued.Add(1, Kind(kind));

    public void Expired(string kind) => _expired.Add(1, Kind(kind));

    public void Abandoned(string kind) =>
        _attempts.Add(1, new TagList { { "kind", kind }, { "outcome", AttemptOutcome.Abandoned } });

    public void Attempt(string kind, string outcome, TimeSpan duration)
    {
        var tags = new TagList { { "kind", kind }, { "outcome", outcome } };
        _attempts.Add(1, tags);
        _duration.Record(duration.TotalSeconds, tags);
    }

    public void AddSampler(Func<OutboxSample?> latest)
    {
        lock (_gate)
        {
            _samples.Add(latest);
        }
    }

    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }

    private List<Measurement<T>> Observe<T>(Func<KindSample, T> value) where T : struct
    {
        Func<OutboxSample?>[] samplers;
        lock (_gate)
        {
            samplers = [.. _samples];
        }

        return [.. samplers
            .Select(latest => latest())
            .OfType<OutboxSample>()
            .SelectMany(sample => sample.Kinds)
            .Select(kind => new Measurement<T>(value(kind), Kind(kind.Kind)))];
    }

    private static TagList Kind(string kind) => new() { { "kind", kind } };
}
