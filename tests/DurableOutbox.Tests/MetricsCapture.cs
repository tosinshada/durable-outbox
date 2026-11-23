using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace DurableOutbox.Tests;

/// <summary>
/// Records the measurements of the given meters only, so tests running in parallel, each with its own
/// container and meter, do not see each other's.
/// </summary>
public sealed class MetricsCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, IReadOnlyDictionary<string, string?> Tags)> _measurements = new();

    public MetricsCapture(params Meter[] meters)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (meters.Contains(instrument.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>Reads the observable gauges now.</summary>
    public void Observe() => _listener.RecordObservableInstruments();

    /// <summary>The values recorded for <paramref name="instrument"/> whose tags include every one given.</summary>
    public double[] Values(string instrument, params (string Key, string Value)[] tags) =>
        [.. _measurements
            .Where(measurement => measurement.Instrument == instrument
                && tags.All(tag => measurement.Tags.TryGetValue(tag.Key, out var value) && value == tag.Value))
            .Select(measurement => measurement.Value)];

    public void Clear() => _measurements.Clear();

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _measurements.Enqueue((
            instrument.Name,
            value,
            tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString())));
}
