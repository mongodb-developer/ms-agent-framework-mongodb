using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using MongoDB.AgentFramework.Tests.RAG;
using System.Diagnostics;

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves <see cref="MongoDBRAGIndexManager"/>'s public entry points each emit exactly one telemetry
/// activity/log using only the authorized operation vocabulary, that the Hybrid variants
/// (<see cref="MongoDBRAGIndexManager.ValidateHybridAsync"/>, <see cref="MongoDBRAGIndexManager.CreateHybridAsync"/>,
/// <see cref="MongoDBRAGIndexManager.EnsureHybridAsync"/>) which internally drive both the Vector and Search
/// index operations never record more than the outer, single activity/log, and that no index name is ever
/// exposed as a telemetry tag or log field.
/// </summary>
public sealed class MongoDBRAGIndexManagerTelemetryTests
{
    [Fact]
    public async Task ListIndexesAsync_RecordsListOperationAndCount()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)],
        };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await manager.ListIndexesAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryFeature.Rag, activity.GetTagItem("feature"));
        Assert.Equal(MongoDBTelemetryOperation.List, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    [Fact]
    public async Task GetVectorSearchIndexAsync_WhenMissing_RecordsEmptyOutcome()
    {
        var state = new RAGCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        MongoDBIndexInfo? index = await manager.GetVectorSearchIndexAsync();

        Assert.Null(index);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.List, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task ValidateVectorSearchIndexAsync_WhenMissing_RecordsFailedOutcome()
    {
        var state = new RAGCollectionState();
        var logger = new RecordingLogger<MongoDBRAGIndexManager>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateVectorManager(state, logger);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => manager.ValidateVectorSearchIndexAsync());

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.ValidateIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.NotNull(activity.GetTagItem("error_category"));

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Assert.All(log.State, pair => Assert.NotEqual("index_name", pair.Key));
    }

    [Fact]
    public async Task ValidateHybridAsync_OnSuccess_RecordsExactlyOneActivityNotTwoOrThree()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3),
                RAGIndexFixtures.ValidSearchIndex("facade_search", textFieldNames: ["text"]),
            ],
        };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        await manager.ValidateHybridAsync();

        // Must not record a nested activity/log for either the Vector or Search validation it internally
        // performs -- only the single outer Hybrid operation.
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.ValidateIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task CreateHybridAsync_OnSuccess_RecordsExactlyOneActivityNotTwoOrThree()
    {
        var state = new RAGCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        await manager.CreateHybridAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.EnsureIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task EnsureVectorSearchIndexAsync_WithWaitUntilReady_RecordsExactlyOneActivityNotTwo()
    {
        var state = new RAGCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await manager.EnsureVectorSearchIndexAsync(waitUntilReady: true);

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.EnsureIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task EnsureHybridAsync_OnSuccess_RecordsExactlyOneActivityNotThree()
    {
        var state = new RAGCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        await manager.EnsureHybridAsync();

        // Internally drives both EnsureVectorSearchIndexCoreAsync and EnsureSearchIndexCoreAsync (each of
        // which could themselves wait for readiness); only the outer Hybrid operation must be recorded.
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.EnsureIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task DropVectorSearchIndexAsync_RecordsDeleteOutcomeAndOmitsIndexName()
    {
        var state = new RAGCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await manager.DropVectorSearchIndexAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Delete, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    [Fact]
    public async Task ValidateVectorSearchIndexAsync_WhenCanceled_RecordsCancelledOutcomeDistinctFromFailed()
    {
        var state = new RAGCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGIndexManager manager = CreateVectorManager(state);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ValidateVectorSearchIndexAsync(cancellationToken: cts.Token));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
    }

    private static MongoDBRAGIndexManager CreateVectorManager(
        RAGCollectionState state, ILogger<MongoDBRAGIndexManager>? logger = null) =>
        new(RAGCollectionProxy.Create(state), VectorDefinition(), searchDefinition: null, logger);

    private static MongoDBRAGIndexManager CreateHybridManager(
        RAGCollectionState state, ILogger<MongoDBRAGIndexManager>? logger = null) =>
        new(RAGCollectionProxy.Create(state), VectorDefinition(), SearchDefinition(), logger);

    private static MongoDBVectorSearchIndexDefinition VectorDefinition() =>
        new("facade_vector", "embedding", 3);

    private static MongoDBSearchIndexDefinition SearchDefinition() =>
        new("facade_search", ["text"]);
}
