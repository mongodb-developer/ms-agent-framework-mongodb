using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Public-seam tests for <see cref="MongoDBRAGIndexManager"/>: the explicit provisioner-role facade over RAG's
/// Vector Search and/or Search indexes. Covers List/Get/Validate/Ensure/Update/WaitUntilReady/Drop for both index
/// kinds, Hybrid requiring both definitions, compatible-vs-actionable mismatch, dynamic-mapping limitations,
/// privilege/deployment error surfacing, idempotent concurrent Ensure, bounded exponential polling with
/// cancellation/deadline, and caller-owned-vs-manager-owned client disposal semantics
/// (docs/spec/features/index-management.md).
/// </summary>
public sealed class MongoDBRAGIndexManagerTests
{
    [Fact]
    public void ConstructorRequiresAtLeastOneDefinition()
    {
        MongoDBConfigurationException exception = Assert.Throws<MongoDBConfigurationException>(
            () => new MongoDBRAGIndexManager(RAGCollectionProxy.Create(new RAGCollectionState())));

        Assert.Contains("vectorDefinition", exception.Message);
    }

    [Fact]
    public async Task GetVectorSearchIndexThrowsConfigurationWhenNotConfigured()
    {
        MongoDBRAGIndexManager manager = new(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            searchDefinition: SearchDefinition());

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() => manager.GetVectorSearchIndexAsync());
    }

    [Fact]
    public async Task GetSearchIndexThrowsConfigurationWhenNotConfigured()
    {
        MongoDBRAGIndexManager manager = new(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            vectorDefinition: VectorDefinition());

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() => manager.GetSearchIndexAsync());
    }

    [Fact]
    public async Task GetVectorSearchIndexReturnsNullWhenMissingAndNeverMutates()
    {
        var state = new RAGCollectionState();
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        Assert.Null(await manager.GetVectorSearchIndexAsync());
        Assert.Null(state.CreatedSearchIndex);
    }

    [Fact]
    public async Task ListIndexesReturnsBothVectorAndSearchIndexes()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex("facade_vector"),
                RAGIndexFixtures.ValidSearchIndex("facade_search"),
            ],
        };
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        IReadOnlyList<MongoDBIndexInfo> indexes = await manager.ListIndexesAsync();

        Assert.Equal(2, indexes.Count);
    }

    [Fact]
    public async Task ValidateVectorThrowsMissingWhenAbsent()
    {
        MongoDBRAGIndexManager manager = CreateVectorManager(new RAGCollectionState());

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => manager.ValidateVectorSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateVectorThrowsMismatchOnWrongDimensions()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex("facade_vector", dimensions: 99)],
        };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(() => manager.ValidateVectorSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateVectorRequiresEveryMandatoryFilterFieldAsFilterType()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex("facade_vector")],
        };
        MongoDBVectorSearchIndexDefinition definition = new(
            "facade_vector", "embedding", 3, filterFieldPaths: ["tenant_id"]);
        MongoDBRAGIndexManager manager = new(RAGCollectionProxy.Create(state), definition);

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => manager.ValidateVectorSearchIndexAsync());
        Assert.Contains("tenant_id", exception.Message);
    }

    [Fact]
    public async Task ValidateSearchTreatsExtraFieldsAsCompatibleDifference()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidSearchIndex(
                "facade_search",
                textFieldNames: ["text"],
                filterFieldTypes: new Dictionary<string, string> { ["priority"] = "number" })],
        };
        MongoDBRAGIndexManager manager = CreateSearchManager(state);

        MongoDBIndexComparison comparison = await manager.ValidateSearchIndexAsync();

        Assert.True(comparison.IsCompatible);
    }

    [Fact]
    public async Task ValidateSearchThrowsMismatchWhenTextFieldNotTextSearchable()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidSearchIndex(
                "facade_search",
                textFieldNames: [],
                filterFieldTypes: new Dictionary<string, string> { ["text"] = "number" })],
        };
        MongoDBRAGIndexManager manager = CreateSearchManager(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(() => manager.ValidateSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateSearchDoesNotThrowForUncheckableDynamicMapping()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.DynamicSearchIndex("facade_search")],
        };
        MongoDBRAGIndexManager manager = CreateSearchManager(state);

        // A dynamic mapping indexes every field automatically; index-management.md requires this be treated as
        // a documented limitation rather than an invented automatic mapping change, so validation must not throw.
        MongoDBIndexComparison comparison = await manager.ValidateSearchIndexAsync();

        Assert.True(comparison.IsCompatible);
    }

    [Fact]
    public async Task ValidateHybridRequiresBothDefinitionsConfigured()
    {
        MongoDBRAGIndexManager manager = CreateVectorManager(new RAGCollectionState());

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() => manager.ValidateHybridAsync());
    }

    [Fact]
    public async Task ValidateHybridValidatesBothIndexes()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex("facade_vector"),
                RAGIndexFixtures.ValidSearchIndex("facade_search"),
            ],
        };
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        await manager.ValidateHybridAsync();
    }

    [Fact]
    public async Task ValidateHybridThrowsWhenOnlyTheVectorIndexIsMissing()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidSearchIndex("facade_search")],
        };
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => manager.ValidateHybridAsync());
    }

    [Fact]
    public async Task EnsureVectorCreatesOnlyWhenExplicitlyCalled()
    {
        var state = new RAGCollectionState();
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        Assert.Null(state.CreatedSearchIndex);
        MongoDBIndexInfo info = await manager.EnsureVectorSearchIndexAsync();

        Assert.NotNull(state.CreatedSearchIndex);
        Assert.Equal("facade_vector", info.Name);
    }

    [Fact]
    public async Task EnsureSearchCreatesOnlyWhenExplicitlyCalled()
    {
        var state = new RAGCollectionState();
        MongoDBRAGIndexManager manager = CreateSearchManager(state);

        MongoDBIndexInfo info = await manager.EnsureSearchIndexAsync();

        Assert.NotNull(state.CreatedSearchIndex);
        Assert.Equal("facade_search", info.Name);
    }

    [Fact]
    public async Task EnsureHybridCreatesBothIndexesAndRequiresBothDefinitions()
    {
        MongoDBRAGIndexManager vectorOnly = CreateVectorManager(new RAGCollectionState());
        await Assert.ThrowsAsync<MongoDBConfigurationException>(() => vectorOnly.EnsureHybridAsync());

        var state = new RAGCollectionState();
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        await manager.EnsureHybridAsync();

        IReadOnlyList<MongoDBIndexInfo> indexes = await manager.ListIndexesAsync();
        Assert.Equal(2, indexes.Count);
    }

    [Fact]
    public async Task EnsureIsIdempotentWhenAConcurrentCallerAlreadyCreatedTheIndex()
    {
        var state = new RAGCollectionState
        {
            CreateException = RAGIndexFixtures.CommandException(68, "index already exists"),
        };
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue([RAGIndexFixtures.ValidVectorIndex("facade_vector")]);
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        MongoDBIndexInfo info = await manager.EnsureVectorSearchIndexAsync();

        Assert.Equal(MongoDBIndexStatus.Ready, info.Status);
    }

    [Fact]
    public async Task ConcurrentEnsureCallsAllSucceedWithExactlyOneWinningCreate()
    {
        var state = new RAGCollectionState();
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        // Several genuinely concurrent Ensure calls race against the same shared fake collection state; the
        // fake's CreateOneAsync handler rejects every loser with an "already exists" failure the way a real
        // server would under a concurrent create race, and every caller must still observe a successful,
        // fully-created index rather than an exception (idempotent Ensure).
        MongoDBIndexInfo[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => manager.EnsureVectorSearchIndexAsync()));

        Assert.All(results, result => Assert.Equal("facade_vector", result.Name));
        Assert.All(results, result => Assert.Equal(MongoDBIndexStatus.Ready, result.Status));
        Assert.True(state.CreateOneCallCount >= 1);
        Assert.Single(state.SearchIndexes);
    }

    [Fact]
    public async Task ConcurrentDropCallsAllSucceedAsIdempotentNoOps()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex("facade_vector")],
        };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.DropVectorSearchIndexAsync()));

        Assert.Equal("facade_vector", state.DroppedIndexName);
    }

    [Fact]
    public async Task EnsureSurfacesPrivilegeErrorTightlyOnCreateFailure()
    {
        var state = new RAGCollectionState
        {
            CreateException = RAGIndexFixtures.CommandException(13, "not authorized on db"),
        };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await Assert.ThrowsAsync<MongoDBIndexPrivilegeException>(() => manager.EnsureVectorSearchIndexAsync());
    }

    [Fact]
    public async Task EnsureSurfacesPersistenceErrorForNonPrivilegeFailure()
    {
        var state = new RAGCollectionState
        {
            CreateException = RAGIndexFixtures.CommandException(999, "server exploded"),
        };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await Assert.ThrowsAsync<MongoDBPersistenceException>(() => manager.EnsureVectorSearchIndexAsync());
    }

    [Fact]
    public async Task EnsureWithWaitUntilReadyPollsThroughBuildingToReady()
    {
        var state = new RAGCollectionState();
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue([]);
        BsonDocument building = RAGIndexFixtures.ValidVectorIndex("facade_vector");
        building["status"] = "BUILDING";
        building["queryable"] = false;
        state.SearchIndexSnapshots.Enqueue([building]);
        state.SearchIndexSnapshots.Enqueue([RAGIndexFixtures.ValidVectorIndex("facade_vector")]);
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        MongoDBIndexInfo info = await manager.EnsureVectorSearchIndexAsync(
            waitUntilReady: true,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Equal(MongoDBIndexStatus.Ready, info.Status);
    }

    [Fact]
    public async Task WaitUntilVectorSearchIndexReadyThrowsStableTimeoutOnDeadline()
    {
        BsonDocument building = RAGIndexFixtures.ValidVectorIndex("facade_vector");
        building["status"] = "BUILDING";
        building["queryable"] = false;
        var state = new RAGCollectionState { SearchIndexes = [building] };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        MongoDBTimeoutException exception = await Assert.ThrowsAsync<MongoDBTimeoutException>(
            () => manager.WaitUntilVectorSearchIndexReadyAsync(
                timeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(1)));

        Assert.IsAssignableFrom<MongoDBIndexException>(exception.InnerException);
    }

    [Fact]
    public async Task WaitUntilVectorSearchIndexReadyThrowsFailedExceptionImmediatelyWithoutPolling()
    {
        BsonDocument failed = RAGIndexFixtures.ValidVectorIndex("facade_vector");
        failed["status"] = "FAILED";
        failed["queryable"] = false;
        var state = new RAGCollectionState { SearchIndexes = [failed] };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        // A terminal Failed build never becomes ready on its own, so this must never be retried: exactly one
        // inspection call is made before the actionable, non-transient failure is thrown, regardless of the
        // configured timeout/pollInterval.
        await Assert.ThrowsAsync<MongoDBIndexFailedException>(
            () => manager.WaitUntilVectorSearchIndexReadyAsync(
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(1, state.SearchIndexListCallCount);
    }

    [Fact]
    public async Task EnsureVectorThrowsFailedExceptionWithoutAutomaticallyRepairingIt()
    {
        BsonDocument failed = RAGIndexFixtures.ValidVectorIndex("facade_vector");
        failed["status"] = "FAILED";
        failed["queryable"] = false;
        var state = new RAGCollectionState { SearchIndexes = [failed] };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        // The Failed index's definition still matches, so isCompatible is true and Ensure never attempts an
        // update -- a terminal build failure is never something Ensure silently repairs; that must be explicit.
        await Assert.ThrowsAsync<MongoDBIndexFailedException>(() => manager.EnsureVectorSearchIndexAsync());

        Assert.Equal(0, state.UpdateCallCount);
        Assert.Null(state.CreatedSearchIndex);
    }

    [Fact]
    public async Task WaitUntilReadyPropagatesCancellation()
    {
        BsonDocument building = RAGIndexFixtures.ValidVectorIndex("facade_vector");
        building["status"] = "BUILDING";
        building["queryable"] = false;
        var state = new RAGCollectionState { SearchIndexes = [building] };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.WaitUntilVectorSearchIndexReadyAsync(
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromSeconds(1),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task UpdateVectorReplacesDefinitionOfAnExistingIndexOnly()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex("facade_vector")],
        };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await manager.UpdateVectorSearchIndexAsync();

        Assert.Equal("facade_vector", state.UpdatedIndexName);
        Assert.NotNull(state.UpdatedDefinition);
    }

    [Fact]
    public async Task UpdateSearchThrowsMissingWhenIndexDoesNotExist()
    {
        var state = new RAGCollectionState();
        MongoDBRAGIndexManager manager = CreateSearchManager(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => manager.UpdateSearchIndexAsync());
        Assert.Equal(0, state.UpdateCallCount);
    }

    [Fact]
    public async Task UpdateSurfacesPrivilegeErrorTightly()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex("facade_vector")],
            UpdateException = RAGIndexFixtures.CommandException(13, "not authorized"),
        };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await Assert.ThrowsAsync<MongoDBIndexPrivilegeException>(() => manager.UpdateVectorSearchIndexAsync());
    }

    [Fact]
    public async Task DropVectorIsIdempotentNoOpWhenAlreadyAbsent()
    {
        var state = new RAGCollectionState
        {
            DropException = RAGIndexFixtures.CommandException(27, "index not found"),
        };
        MongoDBRAGIndexManager manager = CreateVectorManager(state);

        await manager.DropVectorSearchIndexAsync();

        Assert.Equal("facade_vector", state.DroppedIndexName);
        Assert.Equal(1, state.DropOneCallCount);
    }

    [Fact]
    public async Task DropSearchSurfacesPrivilegeErrorTightly()
    {
        var state = new RAGCollectionState
        {
            DropException = RAGIndexFixtures.CommandException(13, "not authorized"),
        };
        MongoDBRAGIndexManager manager = CreateSearchManager(state);

        await Assert.ThrowsAsync<MongoDBIndexPrivilegeException>(() => manager.DropSearchIndexAsync());
    }

    [Fact]
    public async Task ListSurfacesPrivilegeErrorDistinctlyFromCapabilityError()
    {
        var state = new RAGCollectionState
        {
            SearchIndexListException = RAGIndexFixtures.CommandException(13, "not authorized"),
        };
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        await Assert.ThrowsAsync<MongoDBIndexPrivilegeException>(() => manager.ListIndexesAsync());
    }

    [Fact]
    public async Task ListSurfacesCapabilityErrorForNonPrivilegeFailure()
    {
        var state = new RAGCollectionState
        {
            SearchIndexListException = RAGIndexFixtures.CommandException(999, "server exploded"),
        };
        MongoDBRAGIndexManager manager = CreateHybridManager(state);

        await Assert.ThrowsAsync<MongoDBCapabilityException>(() => manager.ListIndexesAsync());
    }

    [Fact]
    public async Task InjectedCollectionRemainsCallerOwned()
    {
        MongoDBRAGIndexManager manager = CreateHybridManager(new RAGCollectionState());

        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.False(manager.OwnsClient);
    }

    [Fact]
    public async Task ConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        MongoDBRAGIndexManager manager = new(
            "mongodb://localhost:27017",
            "database",
            "documents",
            VectorDefinition());

        Assert.True(manager.OwnsClient);
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    private static MongoDBRAGIndexManager CreateVectorManager(RAGCollectionState state) =>
        new(RAGCollectionProxy.Create(state), VectorDefinition());

    private static MongoDBRAGIndexManager CreateSearchManager(RAGCollectionState state) =>
        new(RAGCollectionProxy.Create(state), searchDefinition: SearchDefinition());

    private static MongoDBRAGIndexManager CreateHybridManager(RAGCollectionState state) =>
        new(RAGCollectionProxy.Create(state), VectorDefinition(), SearchDefinition());

    private static MongoDBVectorSearchIndexDefinition VectorDefinition() =>
        new("facade_vector", "embedding", 3);

    private static MongoDBSearchIndexDefinition SearchDefinition() =>
        new("facade_search", ["text"]);
}
