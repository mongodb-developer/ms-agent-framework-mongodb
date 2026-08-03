using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using System.Diagnostics;
using System.Reflection;

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves the closed telemetry allowlist (docs/spec/observability-security.md) holds for every combination of
/// the closed <c>feature</c>/<c>operation</c>/<c>mode</c> vocabulary
/// (<see cref="MongoDBTelemetryFeature"/>/<see cref="MongoDBTelemetryOperation"/>/<see cref="MongoDBTelemetryMode"/>,
/// enumerated by reflection so this test can never drift out of sync with the vocabulary it exercises) and for
/// every <c>outcome</c> (success/empty/failed/cancelled, plus the distinct timeout-classified failure): no
/// activity tag, metric dimension, or structured log state key outside the authorized set is ever present, and
/// a sentinel secret injected into the underlying failure is never present in any tag/dimension/state value,
/// the rendered log message, or the logger's exception argument.
/// </summary>
public sealed class TelemetryAllowlistTests
{
    private const string Secret = "SENTINEL-SECRET-3f9a7c2e5b1d4f68a0c9e2d7b5f1a3c6";

    public static TheoryData<string, string, string?> FeatureOperationModeCombinations()
    {
        var data = new TheoryData<string, string, string?>();
        foreach (string feature in ConstStringValues(typeof(MongoDBTelemetryFeature)))
        {
            foreach (string operation in ConstStringValues(typeof(MongoDBTelemetryOperation)))
            {
                foreach (string? mode in ConstStringValues(typeof(MongoDBTelemetryMode)).Cast<string?>().Append(null))
                {
                    data.Add(feature, operation, mode);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FeatureOperationModeCombinations))]
    public async Task TrackAsync_OnSuccess_EveryFeatureOperationModeCombination_EmitsOnlyAllowedKeys(
        string feature, string operation, string? mode)
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var metric = new MeterCapture(MongoDBTelemetry.MeterName, MongoDBTelemetry.DurationInstrumentName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<TelemetryAllowlistTests>();

        // classifySuccess returns the richest possible successful shape (both a result count and a candidate
        // bucket) so this exercises the widest key set that success can ever legally produce for this
        // combination, not just its narrowest case.
        await MongoDBTelemetry.TrackAsync(
            logger,
            feature,
            operation,
            mode,
            static () => Task.FromResult(5),
            static count => new MongoDBTelemetryResult(
                MongoDBTelemetryOutcome.Success, count, MongoDBCandidateBucket.Bucket(count)),
            CancellationToken.None);

        AssertAllowlisted(activities, metric, logger, scope);
    }

    [Fact]
    public async Task TrackAsync_OnEmpty_EmitsOnlyAllowedKeys()
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var metric = new MeterCapture(MongoDBTelemetry.MeterName, MongoDBTelemetry.DurationInstrumentName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<TelemetryAllowlistTests>();

        await MongoDBTelemetry.TrackAsync(
            logger,
            MongoDBTelemetryFeature.Rag,
            MongoDBTelemetryOperation.Retrieve,
            MongoDBTelemetryMode.HybridRrf,
            static () => Task.FromResult(0),
            static count => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Empty, count, MongoDBCandidateBucket.Bucket(count)),
            CancellationToken.None);

        AssertAllowlisted(activities, metric, logger, scope);
    }

    [Fact]
    public async Task TrackAsync_OnFailure_WithSentinelSecretInException_EmitsOnlyAllowedKeysAndNoSecret()
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var metric = new MeterCapture(MongoDBTelemetry.MeterName, MongoDBTelemetry.DurationInstrumentName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<TelemetryAllowlistTests>();

        await Assert.ThrowsAsync<MongoDBRetrievalException>(() => MongoDBTelemetry.TrackAsync(
            logger,
            MongoDBTelemetryFeature.Memory,
            MongoDBTelemetryOperation.Retrieve,
            MongoDBTelemetryMode.Ann,
            () => Task.FromException<int>(new MongoDBRetrievalException(Secret)),
            static count => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, count, null),
            CancellationToken.None));

        AssertAllowlisted(activities, metric, logger, scope, Secret);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task TrackAsync_OnCancellation_EmitsOnlyAllowedKeysAndDistinctOutcomeFromFailed()
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var metric = new MeterCapture(MongoDBTelemetry.MeterName, MongoDBTelemetry.DurationInstrumentName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<TelemetryAllowlistTests>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => MongoDBTelemetry.TrackAsync(
            logger,
            MongoDBTelemetryFeature.History,
            MongoDBTelemetryOperation.Load,
            mode: null,
            () => Task.FromException<int>(new OperationCanceledException(cts.Token)),
            static count => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, count, null),
            cts.Token));

        AssertAllowlisted(activities, metric, logger, scope);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
    }

    [Fact]
    public async Task TrackAsync_OnTimeout_WithSentinelSecretInException_EmitsOnlyAllowedKeysAndDistinctFromCancelled()
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var metric = new MeterCapture(MongoDBTelemetry.MeterName, MongoDBTelemetry.DurationInstrumentName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<TelemetryAllowlistTests>();

        await Assert.ThrowsAsync<MongoDBTimeoutException>(() => MongoDBTelemetry.TrackAsync(
            logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.Persist,
            mode: null,
            () => Task.FromException<int>(new MongoDBTimeoutException(Secret, new TimeoutException(Secret))),
            static count => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, count, null),
            CancellationToken.None));

        AssertAllowlisted(activities, metric, logger, scope, Secret);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.Equal("timeout", activity.GetTagItem("error_category"));
    }

    private static void AssertAllowlisted(
        ActivityCapture activities, MeterCapture metric, RecordingLogger<TelemetryAllowlistTests> logger,
        TelemetryTestScope scope, params string[] forbiddenValues)
    {
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        TelemetryAllowlist.AssertOnlyAllowedActivityTags(activity, forbiddenValues);

        MeterCapture.Measurement measurement = Assert.Single(metric.MeasurementsUnder(scope));
        TelemetryAllowlist.AssertOnlyAllowedMetricDimensions(measurement, forbiddenValues);

        RecordedLogEntry log = Assert.Single(logger.Entries);
        TelemetryAllowlist.AssertOnlyAllowedLogState(log, forbiddenValues);
    }

    private static IEnumerable<string> ConstStringValues(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string) && field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);
}
