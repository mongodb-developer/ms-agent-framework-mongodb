using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>One captured structured log entry, exposing the same state key/value pairs a real logging
/// provider (e.g. console, OpenTelemetry) would receive -- used to assert exactly which fields and values are
/// emitted, and that nothing else (in particular, no injected sentinel secret) is present.</summary>
internal sealed record RecordedLogEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State);

/// <summary>An <see cref="ILogger{T}"/> test double that records every log call verbatim (no filtering,
/// formatting-only) so tests can assert both structured field values and the full absence of forbidden
/// content across every field, including the rendered message.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _lock = new();
    private readonly List<RecordedLogEntry> _entries = [];

    /// <summary>When set, <see cref="IsEnabled"/> returns this value for every level, letting tests prove
    /// that a disabled logger is never asked to format or allocate log state.</summary>
    public bool? ForcedIsEnabled { get; set; }

    public IReadOnlyList<RecordedLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public bool IsEnabled(LogLevel logLevel) => ForcedIsEnabled ?? true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            throw new InvalidOperationException("Logger was invoked despite being disabled for this level.");
        }

        IReadOnlyList<KeyValuePair<string, object?>> values = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? [.. pairs]
            : [];
        string message = formatter(state, exception);
        lock (_lock)
        {
            _entries.Add(new RecordedLogEntry(logLevel, eventId, message, exception, values));
        }
    }
}

/// <summary>Captures every <see cref="Activity"/> started under a given <see cref="ActivitySource"/> name
/// while in scope, including its final tags -- used to assert span attributes without wiring a real exporter.
/// The underlying <see cref="ActivityListener"/> is process-wide, so xunit's default cross-class test
/// parallelism means unrelated tests' activities can interleave with this capture's; use
/// <see cref="TelemetryTestScope"/> together with <see cref="StoppedUnder"/> to isolate a single test's own
/// activities by trace, rather than asserting against the raw <see cref="Stopped"/> list directly.</summary>
internal sealed class ActivityCapture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];
    private readonly object _lock = new();

    public ActivityCapture(string activitySourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == activitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_lock)
                {
                    _stopped.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Stopped
    {
        get
        {
            lock (_lock)
            {
                return [.. _stopped];
            }
        }
    }

    /// <summary>Returns only the captured activities that belong to the same trace as <paramref name="scope"/>,
    /// filtering out any concurrently-running unrelated test's activities on the same process-wide listener.</summary>
    public IReadOnlyList<Activity> StoppedUnder(TelemetryTestScope scope) =>
        [.. Stopped.Where(activity => activity.RootId == scope.RootId)];

    public void Dispose() => _listener.Dispose();
}

/// <summary>Captures every measurement recorded to a named <see cref="Histogram{T}"/> instrument while in
/// scope, including its tags -- used to assert metric dimensions without wiring a real exporter. Like
/// <see cref="ActivityCapture"/>, the underlying <see cref="MeterListener"/> is process-wide; each measurement
/// is stamped with the <see cref="Activity.Current"/> root id observed synchronously at record time (which
/// <see cref="MongoDBTelemetry"/> always records from within the traced operation's own activity scope) so
/// <see cref="MeasurementsUnder"/> can isolate a single test's own measurements from concurrent unrelated tests.</summary>
internal sealed class MeterCapture : IDisposable
{
    /// <summary>One recorded histogram measurement, the tags it was recorded with, and the ambient activity
    /// root id (if any) observed at the moment of recording.</summary>
    public sealed record Measurement(double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags, string? RootId);

    private readonly MeterListener _listener;
    private readonly List<Measurement> _measurements = [];
    private readonly object _lock = new();

    public MeterCapture(string meterName, string instrumentName)
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            string? rootId = Activity.Current?.RootId;
            lock (_lock)
            {
                _measurements.Add(new Measurement(measurement, [.. tags.ToArray()], rootId));
            }
        });
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_lock)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>Returns only the captured measurements that belong to the same trace as <paramref name="scope"/>,
    /// filtering out any concurrently-running unrelated test's measurements on the same process-wide listener.</summary>
    public IReadOnlyList<Measurement> MeasurementsUnder(TelemetryTestScope scope) =>
        [.. Measurements.Where(measurement => measurement.RootId == scope.RootId)];

    public void Dispose() => _listener.Dispose();
}

/// <summary>Starts a root <see cref="Activity"/> (in W3C id format, independent of any
/// <see cref="ActivitySource"/>/listener) for the duration of a single test, so that any
/// <see cref="MongoDBTelemetry"/> activity/metric produced by code invoked underneath it can be correlated back
/// to this specific test via <see cref="RootId"/> -- isolating it from concurrently-running unrelated tests
/// sharing the same process-wide <see cref="ActivityListener"/>/<see cref="MeterListener"/>.</summary>
internal sealed class TelemetryTestScope : IDisposable
{
    private readonly Activity _root;

    public TelemetryTestScope()
    {
        _root = new Activity(nameof(TelemetryTestScope));
        _root.SetIdFormat(ActivityIdFormat.W3C);
        _root.Start();
    }

    public string? RootId => _root.RootId;

    public void Dispose() => _root.Dispose();
}
