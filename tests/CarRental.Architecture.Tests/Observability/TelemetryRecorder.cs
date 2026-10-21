using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CarRental.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CarRental.Architecture.Tests.Observability;

/// <summary>A measurement of an instrument of the platform meter, with its tags.</summary>
internal sealed record RecordedMeasurement(string Instrument, long Value, IReadOnlyDictionary<string, string?> Tags);

/// <summary>
/// Listens to the instruments of the <c>CarRental</c> meter while it lives. Tests of other classes may record at the same
/// time (tests run in parallel): assertions filter on a tag value of their own.
/// </summary>
internal sealed class TelemetryRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();
    private readonly ConcurrentDictionary<string, Instrument> _instruments = new(StringComparer.Ordinal);

    public TelemetryRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == CarRentalTelemetry.Name)
            {
                _instruments[instrument.Name] = instrument;
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>The instruments of the meter published so far (by name).</summary>
    public IReadOnlyDictionary<string, Instrument> Instruments => _instruments;

    public IReadOnlyList<RecordedMeasurement> Measurements(string instrument) =>
        [.. _measurements.Where(m => string.Equals(m.Instrument, instrument, StringComparison.Ordinal))];

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value?.ToString();
        }

        _measurements.Enqueue(new RecordedMeasurement(instrument.Name, value, copy));
    }
}

/// <summary>An <see cref="ILogger{T}"/> that keeps what is logged.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
