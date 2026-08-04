using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using MongoDB.AgentFramework.Tests.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves <see cref="MongoDBCheckpointStore"/>'s meaningful public operations each emit exactly one telemetry
/// activity/log using only the authorized fields, that a sentinel secret embedded in a simulated driver
/// failure never reaches any log field/message or activity tag, and that cancellation is always recorded as
/// its own distinct outcome rather than a failure.
/// </summary>
public sealed class MongoDBCheckpointStoreTelemetryTests
{
    private const string SentinelSecret = "SENTINEL-SECRET-3f7b1e9c5a2d8046b9e3f1c7a5d0b2e8";

    [Fact]
    public async Task SaveCheckpointAsync_OnSuccess_RecordsPersistOutcomeAndCount()
    {
        var state = new CheckpointCollectionState();
        var logger = new RecordingLogger<MongoDBCheckpointStore>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state, logger: logger);

        await store.SaveCheckpointAsync("session-1", "checkpoint-1", JsonSerializer.SerializeToElement("value"));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryFeature.CheckpointStore, activity.GetTagItem("feature"));
        Assert.Equal(MongoDBTelemetryOperation.Persist, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Dictionary<string, object?> fields = log.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(MongoDBTelemetryOperation.Persist, fields["operation"]);
        Assert.Equal(MongoDBTelemetryOutcome.Success, fields["outcome"]);
    }

    [Fact]
    public async Task LoadCheckpointAsync_OnSuccess_RecordsLoadOutcomeAndCount()
    {
        var state = new CheckpointCollectionState();
        MongoDBCheckpointStore seeder = CreateStore(state);
        await seeder.SaveCheckpointAsync("session-2", "checkpoint-1", JsonSerializer.SerializeToElement("value"));
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        MongoDBCheckpointRecord? record = await store.LoadCheckpointAsync("session-2", "checkpoint-1");

        Assert.NotNull(record);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Load, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task LoadCheckpointAsync_WhenAbsent_RecordsEmptyOutcome()
    {
        var state = new CheckpointCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        MongoDBCheckpointRecord? record = await store.LoadCheckpointAsync("session-3", "missing-checkpoint");

        Assert.Null(record);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task GetLatestCheckpointAsync_OnSuccess_RecordsLoadOutcomeAndCount()
    {
        var state = new CheckpointCollectionState();
        MongoDBCheckpointStore seeder = CreateStore(state);
        await seeder.SaveCheckpointAsync("session-4", "checkpoint-1", JsonSerializer.SerializeToElement("value"));
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        MongoDBCheckpointRecord? record = await store.GetLatestCheckpointAsync("session-4");

        Assert.NotNull(record);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Load, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task ListCheckpointsAsync_OnSuccess_RecordsListOutcomeAndCount()
    {
        var state = new CheckpointCollectionState();
        MongoDBCheckpointStore seeder = CreateStore(state);
        await seeder.SaveCheckpointAsync("session-5", "checkpoint-1", JsonSerializer.SerializeToElement("value"));
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        MongoDBCheckpointPage page = await store.ListCheckpointsAsync("session-5", limit: 10);

        Assert.Single(page.Items);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.List, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task ListCheckpointsAsync_WhenEmpty_RecordsEmptyOutcome()
    {
        var state = new CheckpointCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        MongoDBCheckpointPage page = await store.ListCheckpointsAsync("session-6", limit: 10);

        Assert.Empty(page.Items);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task DeleteCheckpointAsync_OnSuccess_RecordsDeleteOutcomeAndCount()
    {
        var state = new CheckpointCollectionState();
        MongoDBCheckpointStore seeder = CreateStore(state);
        await seeder.SaveCheckpointAsync("session-7", "checkpoint-1", JsonSerializer.SerializeToElement("value"));
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        bool deleted = await store.DeleteCheckpointAsync("session-7", "checkpoint-1");

        Assert.True(deleted);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Delete, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task DeleteCheckpointAsync_WhenAbsent_RecordsEmptyOutcome()
    {
        var state = new CheckpointCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        bool deleted = await store.DeleteCheckpointAsync("session-8", "missing-checkpoint");

        Assert.False(deleted);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task EnsureIndexesAsync_RecordsEnsureIndexOperationAndOmitsIndexName()
    {
        var state = new CheckpointCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        await store.EnsureIndexesAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.EnsureIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    [Fact]
    public async Task ValidateIndexesAsync_RecordsValidateIndexOperation()
    {
        var state = new CheckpointCollectionState();
        MongoDBCheckpointStore seeder = CreateStore(state);
        await seeder.EnsureIndexesAsync();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);

        await store.ValidateIndexesAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.ValidateIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task SaveCheckpointAsync_WhenDriverThrowsWithSentinelSecret_NeverLeaksSecretAndClassifiesPersistence()
    {
        var state = new CheckpointCollectionState { InsertException = OfflineException(SentinelSecret) };
        var logger = new RecordingLogger<MongoDBCheckpointStore>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state, logger: logger);

        await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => store.SaveCheckpointAsync("session-9", "checkpoint-1", JsonSerializer.SerializeToElement("value")));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.Equal("persistence", activity.GetTagItem("error_category"));
        foreach (KeyValuePair<string, string?> tag in activity.TagObjects.Select(
            t => new KeyValuePair<string, string?>(t.Key, t.Value?.ToString())))
        {
            Assert.DoesNotContain(SentinelSecret, tag.Value ?? string.Empty, StringComparison.Ordinal);
        }

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Assert.DoesNotContain(SentinelSecret, log.Message, StringComparison.Ordinal);
        foreach (object? value in log.State.Select(pair => pair.Value))
        {
            Assert.DoesNotContain(SentinelSecret, value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SaveCheckpointAsync_WhenPersistenceDeadlineElapses_RecordsFailedTimeoutNotCancelled()
    {
        // A hung sequence-allocation read only ever completes once the deadline-linked token fires (the
        // caller's own token is never cancelled here): telemetry must observe the already-translated
        // MongoDBTimeoutException, not the raw deadline-driven OperationCanceledException.
        var state = new CheckpointCollectionState
        {
            FindDelay = async token => await Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state, persistenceTimeout: TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<MongoDBTimeoutException>(
            () => store.SaveCheckpointAsync(
                "session-timeout", "checkpoint-1", JsonSerializer.SerializeToElement("value")));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.Equal("timeout", activity.GetTagItem("error_category"));
    }

    [Fact]
    public async Task CreateCheckpointAsync_WhenPersistenceDeadlineElapses_RecordsFailedTimeoutNotCancelled()
    {
        var state = new CheckpointCollectionState
        {
            FindDelay = async token => await Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state, persistenceTimeout: TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<MongoDBTimeoutException>(() => store.CreateCheckpointAsync(
            "session-timeout-2", JsonSerializer.SerializeToElement("value")).AsTask());

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.Equal("timeout", activity.GetTagItem("error_category"));
    }

    [Fact]
    public async Task LoadCheckpointAsync_WhenCanceled_RecordsCancelledOutcomeDistinctFromFailed()
    {
        var state = new CheckpointCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBCheckpointStore store = CreateStore(state);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.LoadCheckpointAsync("session-10", "checkpoint-1", cts.Token));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
    }

    private static MongoDBCheckpointStore CreateStore(
        CheckpointCollectionState state,
        ILogger<MongoDBCheckpointStore>? logger = null,
        TimeSpan? persistenceTimeout = null) =>
        new(
            CheckpointCollectionProxy.Create(state),
            new MongoDBCheckpointStoreOptions
            {
                WorkflowId = "workflow",
                ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
                PersistenceTimeout = persistenceTimeout,
            },
            logger);

    private static MongoCommandException OfflineException(string message) =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "insert",
            new BsonDocument(),
            new BsonDocument
            {
                { "ok", 0 },
                { "code", 50 },
                { "errmsg", message },
            });
}
