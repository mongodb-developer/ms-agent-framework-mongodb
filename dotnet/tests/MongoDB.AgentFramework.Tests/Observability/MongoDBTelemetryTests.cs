using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using System.Diagnostics;

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves the shared telemetry helper emits exactly the fields authorized by
/// docs/spec/observability-security.md (feature, operation, mode, outcome, result count, candidate bucket,
/// error category) and never anything else -- in particular never an exception message, even when the
/// underlying exception carries a sentinel secret designed to catch a leak.
/// </summary>
public sealed class MongoDBTelemetryTests
{
    private const string SentinelSecret = "SENTINEL-SECRET-6b6f77c6a1f34e6d9a9f5b6d2b7d9a11";

    [Fact]
    public async Task TrackAsync_OnSuccess_RecordsOneActivityOneLogAndOneMetric()
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var metric = new MeterCapture(MongoDBTelemetry.MeterName, MongoDBTelemetry.DurationInstrumentName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<MongoDBTelemetryTests>();

        int result = await MongoDBTelemetry.TrackAsync(
            logger,
            MongoDBTelemetryFeature.Memory,
            MongoDBTelemetryOperation.Retrieve,
            MongoDBTelemetryMode.Ann,
            static () => Task.FromResult(3),
            static count => new MongoDBTelemetryResult(
                count > 0 ? MongoDBTelemetryOutcome.Success : MongoDBTelemetryOutcome.Empty,
                count,
                MongoDBCandidateBucket.Bucket(25)),
            CancellationToken.None);

        Assert.Equal(3, result);
        Assert.Single(activities.StoppedUnder(scope));
        Assert.Single(metric.MeasurementsUnder(scope));
        Assert.Single(logger.Entries);

        Activity activity = activities.StoppedUnder(scope)[0];
        Assert.Equal(MongoDBTelemetryFeature.Memory, activity.GetTagItem("feature"));
        Assert.Equal(MongoDBTelemetryOperation.Retrieve, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryMode.Ann, activity.GetTagItem("mode"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(3, activity.GetTagItem("result_count"));
        Assert.Equal("11-100", activity.GetTagItem("candidate_bucket"));
        Assert.Null(activity.GetTagItem("error_category"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);

        MeterCapture.Measurement measurement = metric.MeasurementsUnder(scope)[0];
        Dictionary<string, object?> tags = measurement.Tags.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(MongoDBTelemetryFeature.Memory, tags["feature"]);
        Assert.Equal(MongoDBTelemetryOperation.Retrieve, tags["operation"]);
        Assert.Equal(MongoDBTelemetryMode.Ann, tags["mode"]);
        Assert.Equal(MongoDBTelemetryOutcome.Success, tags["outcome"]);
        Assert.False(tags.ContainsKey("result_count"), "Result count is high-cardinality and must not be a metric dimension.");
        Assert.True(measurement.Value >= 0);

        RecordedLogEntry log = logger.Entries[0];
        Assert.Equal(LogLevel.Information, log.Level);
        Dictionary<string, object?> state = log.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(MongoDBTelemetryFeature.Memory, state["feature"]);
        Assert.Equal(MongoDBTelemetryOperation.Retrieve, state["operation"]);
        Assert.Equal(MongoDBTelemetryMode.Ann, state["mode"]);
        Assert.Equal(MongoDBTelemetryOutcome.Success, state["outcome"]);
        Assert.Equal(3, state["result_count"]);
        Assert.Equal("11-100", state["candidate_bucket"]);
    }

    [Fact]
    public async Task TrackAsync_WhenActionThrowsOperationCanceled_RecordsCancelledOutcomeAndRethrows()
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<MongoDBTelemetryTests>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => MongoDBTelemetry.TrackAsync(
            logger,
            MongoDBTelemetryFeature.Rag,
            MongoDBTelemetryOperation.Retrieve,
            MongoDBTelemetryMode.HybridRrf,
            () => Task.FromException<int>(new OperationCanceledException(cts.Token)),
            static count => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, count, null),
            cts.Token));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Dictionary<string, object?> state = log.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, state["outcome"]);
        Assert.False(state.ContainsKey("error_category"));
    }

    public static TheoryData<Func<Exception>, string> ClassifiedExceptions() => new()
    {
        { () => new MongoDBConfigurationException(SentinelSecret), "configuration" },
        { () => new MongoDBEmbeddingException(SentinelSecret), "embedding" },
        { () => new MongoDBCapabilityException(SentinelSecret), "capability" },
        { () => new MongoDBIndexMissingException(SentinelSecret), "index_missing" },
        { () => new MongoDBIndexMismatchException(SentinelSecret), "index_mismatch" },
        { () => new MongoDBIndexNotReadyException(SentinelSecret), "index_not_ready" },
        { () => new MongoDBIndexFailedException(SentinelSecret), "index_failed" },
        { () => new MongoDBIndexAlreadyExistsException(SentinelSecret), "index_already_exists" },
        { () => new MongoDBIndexPrivilegeException(SentinelSecret), "index_privilege" },
        { () => new MongoDBIndexException(SentinelSecret), "index_other" },
        { () => new MongoDBMappingException(SentinelSecret), "mapping" },
        { () => new MongoDBRetrievalException(SentinelSecret), "retrieval" },
        { () => new MongoDBPersistenceException(SentinelSecret), "persistence" },
        { () => new MongoDBTimeoutException(SentinelSecret, new TimeoutException(SentinelSecret)), "timeout" },
        { () => new MongoDBConcurrencyException(SentinelSecret), "concurrency" },
        { () => new ArgumentException(SentinelSecret), "configuration" },
        { () => new InvalidOperationException(SentinelSecret), "unknown" },
    };

    [Theory]
    [MemberData(nameof(ClassifiedExceptions))]
    public async Task TrackAsync_WhenActionThrows_ClassifiesErrorCategoryAndNeverLeaksMessage(
        Func<Exception> createException, string expectedCategory)
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<MongoDBTelemetryTests>();
        Exception exception = createException();

        await Assert.ThrowsAnyAsync<Exception>(() => MongoDBTelemetry.TrackAsync(
            logger,
            MongoDBTelemetryFeature.SessionStore,
            MongoDBTelemetryOperation.Persist,
            mode: null,
            () => Task.FromException<int>(exception),
            static count => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, count, null),
            CancellationToken.None));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.Equal(expectedCategory, activity.GetTagItem("error_category"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        AssertNoSecret(activity);

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, log.Level);
        Dictionary<string, object?> state = log.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(MongoDBTelemetryOutcome.Failed, state["outcome"]);
        Assert.Equal(expectedCategory, state["error_category"]);
        AssertNoSecret(state.Values);
        Assert.DoesNotContain(SentinelSecret, log.Message, StringComparison.Ordinal);
        Assert.Null(log.Exception);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1-10")]
    [InlineData(10, "1-10")]
    [InlineData(11, "11-100")]
    [InlineData(100, "11-100")]
    [InlineData(101, "101-1000")]
    [InlineData(1000, "101-1000")]
    [InlineData(1001, "1000+")]
    [InlineData(1_000_000, "1000+")]
    public void CandidateBucket_BucketsValuesIntoStableRanges(int rawCandidateCount, string expectedBucket)
    {
        Assert.Equal(expectedBucket, MongoDBCandidateBucket.Bucket(rawCandidateCount));
    }

    [Fact]
    public void CandidateBucket_ReturnsNullForNoCandidateConcept()
    {
        Assert.Null(MongoDBCandidateBucket.Bucket(null));
    }

    [Fact]
    public async Task TrackAsync_WhenLoggerDisabled_NeverInvokesLoggerAndSkipsMessageFormatting()
    {
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        var logger = new RecordingLogger<MongoDBTelemetryTests> { ForcedIsEnabled = false };

        int result = await MongoDBTelemetry.TrackAsync(
            logger,
            MongoDBTelemetryFeature.History,
            MongoDBTelemetryOperation.Load,
            mode: null,
            static () => Task.FromResult(1),
            static count => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, count, null),
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(logger.Entries);
        // The activity is still recorded when a listener is attached (tracing and logging are independent
        // concerns); when no listener is attached, ActivitySource.StartActivity short-circuits to null with
        // negligible overhead, which is exercised implicitly by every non-traced production call.
        Assert.Single(activities.StoppedUnder(scope));
    }

    private static void AssertNoSecret(Activity activity)
    {
        foreach (KeyValuePair<string, string?> tag in activity.TagObjects.Select(
            t => new KeyValuePair<string, string?>(t.Key, t.Value?.ToString())))
        {
            Assert.DoesNotContain(SentinelSecret, tag.Value ?? string.Empty, StringComparison.Ordinal);
        }
    }

    private static void AssertNoSecret(IEnumerable<object?> values)
    {
        foreach (object? value in values)
        {
            Assert.DoesNotContain(SentinelSecret, value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
