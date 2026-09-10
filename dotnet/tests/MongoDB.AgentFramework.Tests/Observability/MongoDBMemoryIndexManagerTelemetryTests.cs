using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using MongoDB.AgentFramework.Tests.Memory;
using System.Diagnostics;

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves <see cref="MongoDBMemoryIndexManager"/>'s public entry points each emit exactly one telemetry
/// activity/log using only the authorized operation vocabulary, that <see cref="MongoDBMemoryIndexManager.EnsureIndexAsync"/>
/// waiting for readiness never produces a duplicate nested activity/log for its internal wait, and that no
/// index name is ever exposed as a telemetry tag or log field.
/// </summary>
public sealed class MongoDBMemoryIndexManagerTelemetryTests
{
    [Fact]
    public async Task ListIndexesAsync_RecordsListOperationAndCount()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)],
        };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await manager.ListIndexesAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryFeature.Memory, activity.GetTagItem("feature"));
        Assert.Equal(MongoDBTelemetryOperation.List, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    [Fact]
    public async Task GetIndexAsync_WhenMissing_RecordsEmptyOutcome()
    {
        var state = new MemoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBIndexInfo? index = await manager.GetIndexAsync();

        Assert.Null(index);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.List, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task ValidateIndexAsync_WhenMissing_RecordsFailedOutcome()
    {
        var state = new MemoryCollectionState();
        var logger = new RecordingLogger<MongoDBMemoryIndexManager>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryIndexManager manager = CreateManager(state, logger);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => manager.ValidateIndexAsync());

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.ValidateIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.NotNull(activity.GetTagItem("error_category"));

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Assert.DoesNotContain("facade_vector", log.Message, StringComparison.Ordinal);
        Assert.All(log.State, pair => Assert.NotEqual("index_name", pair.Key));
    }

    [Fact]
    public async Task EnsureIndexAsync_WithWaitUntilReady_RecordsExactlyOneActivityNotTwo()
    {
        var state = new MemoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await manager.EnsureIndexAsync(waitUntilReady: true);

        // The internal readiness wait must reuse EnsureIndexAsync's own outer activity/log rather than
        // recording a second, nested one for WaitUntilReadyCoreAsync.
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.EnsureIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task DropIndexAsync_RecordsDeleteOutcomeAndOmitsIndexName()
    {
        var state = new MemoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await manager.DropIndexAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Delete, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    [Fact]
    public async Task ValidateIndexAsync_WhenCanceled_RecordsCancelledOutcomeDistinctFromFailed()
    {
        var state = new MemoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryIndexManager manager = CreateManager(state);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ValidateIndexAsync(cancellationToken: cts.Token));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
    }

    private static MongoDBMemoryIndexManager CreateManager(
        MemoryCollectionState state, ILogger<MongoDBMemoryIndexManager>? logger = null) =>
        new(MemoryCollectionProxy.Create(state), Definition(), logger);

    private static MongoDBVectorSearchIndexDefinition Definition() =>
        new("facade_vector", "embedding", 3);
}
