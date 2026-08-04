using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MongoDB.AgentFramework.Internal.Observability;

/// <summary>
/// Shared instrumentation for every provider/store's meaningful public operations, wired through the public
/// <see cref="System.Diagnostics.ActivitySource"/>/<see cref="System.Diagnostics.Metrics.Meter"/> conventions
/// and <see cref="Microsoft.Extensions.Logging.ILogger"/> -- never a parallel or proprietary telemetry system,
/// and never an exporter or telemetry backend of its own (docs/decisions/0017-use-standard-telemetry-without-unapproved-markers.md).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TrackAsync{T}"/> is the single call site every instrumented operation goes through. It records
/// exactly one <see cref="Activity"/>, one duration measurement, and one structured completion log per
/// invocation, using only the stable, low-cardinality fields authorized by
/// docs/spec/observability-security.md: feature, operation, mode, outcome, result count, candidate bucket, and
/// error category. It never records database/collection/host names, query text, filter values, document or
/// tenant/user identifiers, source URLs, raw BSON, embeddings, message content, index names, or an exception's
/// message -- the wrapped action's own return value and the classifier delegates are the only source of any
/// recorded field, and neither ever receives the caught exception itself, only its type.
/// </para>
/// <para>
/// When nothing is listening (no <see cref="ActivityListener"/> subscribed to <see cref="ActivitySourceName"/>
/// and no <see cref="Meter"/> listener enabled), <c>ActivitySource.StartActivity</c> and the
/// histogram's <c>Record</c> call are both no-ops from the runtime's own design, so a disabled pipeline costs
/// only the classifier delegate invocation and a single <see cref="ILogger.IsEnabled"/> check -- never a
/// message allocation or formatting pass, since the log call itself is skipped entirely when the configured
/// minimum level excludes it.
/// </para>
/// </remarks>
internal static class MongoDBTelemetry
{
    /// <summary>The <see cref="ActivitySource"/>/<see cref="Meter"/> name every MongoDB Agent Framework
    /// operation shares. A consumer wires this into whatever OpenTelemetry (or other) pipeline it already
    /// runs; this project never exports telemetry itself.</summary>
    public const string ActivitySourceName = "MongoDB.AgentFramework";

    /// <summary>Alias kept distinct from <see cref="ActivitySourceName"/> in call sites for readability; both
    /// currently share the same string, matching the existing convention of naming the meter after the
    /// activity source it accompanies.</summary>
    public const string MeterName = ActivitySourceName;

    /// <summary>The name of the single duration histogram instrument every operation reports to.</summary>
    public const string DurationInstrumentName = "mongodb.agentframework.operation.duration";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter OperationMeter = new(MeterName);

    private static readonly Histogram<double> Duration = OperationMeter.CreateHistogram<double>(
        DurationInstrumentName,
        unit: "ms",
        description: "Duration of a MongoDB Agent Framework operation, in milliseconds.");

    /// <summary>The shared <see cref="ActivitySource"/>. Exposed only so a provider that must start a span
    /// with a different name shape than <see cref="TrackAsync{T}"/> assumes can still share the same source;
    /// every current call site goes through <see cref="TrackAsync{T}"/> instead.</summary>
    public static ActivitySource ActivitySource => Source;

    /// <summary>
    /// Runs <paramref name="action"/>, recording exactly one activity, one duration measurement, and one
    /// structured completion log describing how it completed.
    /// </summary>
    /// <typeparam name="T">The wrapped action's result type.</typeparam>
    /// <param name="logger">The owning provider/store's logger.</param>
    /// <param name="feature">A <see cref="MongoDBTelemetryFeature"/> value.</param>
    /// <param name="operation">A <see cref="MongoDBTelemetryOperation"/> value.</param>
    /// <param name="mode">A <see cref="MongoDBTelemetryMode"/> value, or <see langword="null"/> if the
    /// operation has no retrieval-mode concept.</param>
    /// <param name="action">The operation to run and time.</param>
    /// <param name="classifySuccess">Derives the <see cref="MongoDBTelemetryResult"/> from a successful
    /// result. Never invoked when <paramref name="action"/> throws.</param>
    /// <param name="cancellationToken">Unused by this helper directly; accepted so call sites can pass the
    /// same token they gave <paramref name="action"/> for clarity at the call site. Cancellation is always
    /// recognized by catching <see cref="OperationCanceledException"/>, regardless of which token raised it.</param>
    public static async Task<T> TrackAsync<T>(
        ILogger logger,
        string feature,
        string operation,
        string? mode,
        Func<Task<T>> action,
        Func<T, MongoDBTelemetryResult> classifySuccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(classifySuccess);
        _ = cancellationToken;

        using Activity? activity = Source.StartActivity($"mongodb.{feature}.{operation}");
        activity?.SetTag("feature", feature);
        activity?.SetTag("operation", operation);
        if (mode is not null)
        {
            activity?.SetTag("mode", mode);
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            T result = await action().ConfigureAwait(false);
            MongoDBTelemetryResult classified = classifySuccess(result);
            RecordCompletion(logger, activity, feature, operation, mode, classified, errorCategory: null, startTimestamp);
            return result;
        }
        catch (OperationCanceledException)
        {
            RecordCompletion(
                logger, activity, feature, operation, mode,
                new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Cancelled, null, null),
                errorCategory: null,
                startTimestamp);
            throw;
        }
        catch (Exception exception)
        {
            string category = MongoDBErrorCategory.Classify(exception);
            RecordCompletion(
                logger, activity, feature, operation, mode,
                new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Failed, null, null),
                category,
                startTimestamp);
            throw;
        }
    }

    /// <summary>Overload for operations with no meaningful return value.</summary>
    public static Task TrackAsync(
        ILogger logger,
        string feature,
        string operation,
        string? mode,
        Func<Task> action,
        Func<MongoDBTelemetryResult> classifySuccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(classifySuccess);
        return TrackAsync(
            logger,
            feature,
            operation,
            mode,
            async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            },
            _ => classifySuccess(),
            cancellationToken);
    }

    private static void RecordCompletion(
        ILogger logger,
        Activity? activity,
        string feature,
        string operation,
        string? mode,
        MongoDBTelemetryResult result,
        string? errorCategory,
        long startTimestamp)
    {
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        activity?.SetTag("outcome", result.Outcome);
        if (result.ResultCount is int resultCount)
        {
            activity?.SetTag("result_count", resultCount);
        }

        if (result.CandidateBucket is not null)
        {
            activity?.SetTag("candidate_bucket", result.CandidateBucket);
        }

        if (errorCategory is not null)
        {
            activity?.SetTag("error_category", errorCategory);
        }

        activity?.SetStatus(
            result.Outcome is MongoDBTelemetryOutcome.Failed or MongoDBTelemetryOutcome.Cancelled
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok);

        TagList metricTags = default;
        metricTags.Add("feature", feature);
        metricTags.Add("operation", operation);
        if (mode is not null)
        {
            metricTags.Add("mode", mode);
        }

        metricTags.Add("outcome", result.Outcome);
        if (errorCategory is not null)
        {
            metricTags.Add("error_category", errorCategory);
        }

        Duration.Record(elapsedMilliseconds, metricTags);

        LogLevel level = result.Outcome == MongoDBTelemetryOutcome.Failed ? LogLevel.Warning : LogLevel.Information;
        if (!logger.IsEnabled(level))
        {
            return;
        }

        var state = new List<KeyValuePair<string, object?>>
        {
            new("feature", feature),
            new("operation", operation),
        };
        if (mode is not null)
        {
            state.Add(new("mode", mode));
        }

        state.Add(new("outcome", result.Outcome));
        if (result.ResultCount is int loggedResultCount)
        {
            state.Add(new("result_count", loggedResultCount));
        }

        if (result.CandidateBucket is not null)
        {
            state.Add(new("candidate_bucket", result.CandidateBucket));
        }

        if (errorCategory is not null)
        {
            state.Add(new("error_category", errorCategory));
        }

        state.Add(new("duration_ms", elapsedMilliseconds));

        logger.Log(
            level,
            eventId: default,
            state,
            exception: null,
            static (loggedState, _) => FormatMessage(loggedState));
    }

    private static string FormatMessage(List<KeyValuePair<string, object?>> state)
    {
        string fields = string.Join(' ', state.Select(pair => $"{pair.Key}={pair.Value}"));
        return $"MongoDB operation completed. {fields}";
    }
}
