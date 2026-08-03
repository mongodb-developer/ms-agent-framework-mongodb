using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.Memory;

/// <summary>
/// Public-seam tests for <see cref="MongoDBMemoryIndexManager"/>: the explicit provisioner-role facade over
/// Memory's Vector Search index. Covers List/Get/Validate/Ensure/Update/WaitUntilReady/Drop, compatible-vs-
/// actionable mismatch, privilege/deployment error surfacing, idempotent concurrent Ensure, bounded exponential
/// polling with cancellation/deadline, and caller-owned-vs-manager-owned client disposal semantics
/// (docs/spec/features/index-management.md).
/// </summary>
public sealed class MongoDBMemoryIndexManagerTests
{
    [Fact]
    public async Task GetIndexReturnsNullWhenMissingAndNeverMutates()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBIndexInfo? index = await manager.GetIndexAsync();

        Assert.Null(index);
        Assert.Null(state.CreatedSearchIndex);
        Assert.Equal(0, state.CreateOneCallCount);
    }

    [Fact]
    public async Task GetIndexReturnsInspectedSnapshotWhenPresent()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBIndexInfo? index = await manager.GetIndexAsync();

        Assert.NotNull(index);
        Assert.Equal("facade_vector", index!.Name);
        Assert.Equal(MongoDBIndexStatus.Ready, index.Status);
        Assert.True(index.Queryable);
    }

    [Fact]
    public async Task ListIndexesReturnsEveryIndexWithoutMutating()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes =
            [
                MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3),
                MemoryIndexFixtures.ValidVectorIndex("other_index", "embedding", 3),
            ],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        IReadOnlyList<MongoDBIndexInfo> indexes = await manager.ListIndexesAsync();

        Assert.Equal(2, indexes.Count);
        Assert.Null(state.CreatedSearchIndex);
    }

    [Fact]
    public async Task ValidateThrowsMissingWhenIndexDoesNotExist()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => manager.ValidateIndexAsync());
    }

    [Fact]
    public async Task ValidateDistinguishesActionableMismatchFromCompatibleDifference()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex(
                "facade_vector", "embedding", 3, filterFieldPaths: "extra_field")],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        // An extra, unrequired filter field is a compatible difference (does not break the manager's own
        // required filter fields), so validation must succeed rather than throw.
        MongoDBIndexComparison comparison = await manager.ValidateIndexAsync();

        Assert.True(comparison.IsCompatible);
        Assert.Contains(comparison.CompatibleDifferences, d => d.Contains("extra_field"));
    }

    [Fact]
    public async Task ValidateThrowsMismatchForWrongDimensions()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 99)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => manager.ValidateIndexAsync());
        Assert.Contains("dimensions", exception.Message);
    }

    [Fact]
    public async Task ValidateThrowsNotReadyWhenBuildingAndRequireReadyIsTrue()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex(
                "facade_vector", "embedding", 3, status: "BUILDING", queryable: false)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(() => manager.ValidateIndexAsync());
        // requireReady: false must tolerate the same not-yet-queryable index.
        MongoDBIndexComparison comparison = await manager.ValidateIndexAsync(requireReady: false);
        Assert.True(comparison.IsCompatible);
    }

    [Fact]
    public async Task EnsureCreatesIndexOnlyWhenExplicitlyCalledAndNeverOnGetOrValidate()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await manager.GetIndexAsync();
        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => manager.ValidateIndexAsync());
        Assert.Null(state.CreatedSearchIndex);

        MongoDBIndexInfo info = await manager.EnsureIndexAsync();

        Assert.NotNull(state.CreatedSearchIndex);
        Assert.Equal("facade_vector", info.Name);
    }

    [Fact]
    public async Task EnsureIsIdempotentWhenAConcurrentCallerAlreadyCreatedTheIndex()
    {
        var state = new MemoryCollectionState
        {
            CreateException = MemoryIndexFixtures.CommandException(
                68, "IndexAlreadyExists", "Index already exists"),
        };
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue([MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)]);
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBIndexInfo info = await manager.EnsureIndexAsync();

        Assert.Equal("facade_vector", info.Name);
        Assert.Equal(MongoDBIndexStatus.Ready, info.Status);
    }

    [Fact]
    public async Task ConcurrentEnsureCallsAllSucceedWithExactlyOneWinningCreate()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        // Several genuinely concurrent Ensure calls race against the same shared fake collection state; the
        // fake's CreateOneAsync handler rejects every loser with an "already exists" failure the way a real
        // server would under a concurrent create race, and every caller must still observe a successful,
        // fully-created index rather than an exception (idempotent Ensure).
        MongoDBIndexInfo[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => manager.EnsureIndexAsync()));

        Assert.All(results, result => Assert.Equal("facade_vector", result.Name));
        Assert.All(results, result => Assert.Equal(MongoDBIndexStatus.Ready, result.Status));
        Assert.True(state.CreateOneCallCount >= 1);
        Assert.Single(state.SearchIndexes);
    }

    [Fact]
    public async Task ConcurrentDropCallsAllSucceedAsIdempotentNoOps()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        // The first concurrent DropOneAsync succeeds; the fake's DropException is not configured, so all others
        // succeed too in this simplified sequential-fake model, but this still proves the facade never throws
        // for a redundant concurrent drop.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.DropIndexAsync()));

        Assert.Equal("facade_vector", state.DroppedIndexName);
    }

    [Fact]
    public async Task CreateSucceedsWhenTheIndexIsMissing()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBIndexInfo info = await manager.CreateIndexAsync();

        Assert.NotNull(state.CreatedSearchIndex);
        Assert.Equal("facade_vector", info.Name);
        Assert.Equal(MongoDBIndexStatus.Ready, info.Status);
    }

    [Fact]
    public async Task CreateFailsImmediatelyWhenTheIndexAlreadyExistsWithoutAttemptingTheDriverCall()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        // Create-only pre-checks existence before ever calling CreateOneAsync: an explicit create-only caller is
        // told something was already there rather than silently proceeding (unlike the idempotent Ensure path).
        await Assert.ThrowsAsync<MongoDBIndexAlreadyExistsException>(() => manager.CreateIndexAsync());

        Assert.Equal(0, state.CreateOneCallCount);
    }

    [Fact]
    public async Task CreateFailsWhenAConcurrentCallerWinsTheCreateRace()
    {
        var state = new MemoryCollectionState
        {
            CreateException = MemoryIndexFixtures.CommandException(
                68, "IndexAlreadyExists", "Index already exists"),
        };
        state.SearchIndexSnapshots.Enqueue([]);
        MongoDBMemoryIndexManager manager = CreateManager(state);

        // The pre-check found nothing, but a rival caller won the create race in between: the driver call itself
        // reports "already exists", which create-only must still surface (never silently swallowed the way
        // Ensure's idempotent create is).
        await Assert.ThrowsAsync<MongoDBIndexAlreadyExistsException>(() => manager.CreateIndexAsync());

        Assert.Equal(1, state.CreateOneCallCount);
    }

    [Fact]
    public async Task EnsureThrowsMismatchWhenARivalConcurrentCreateWonWithAnIncompatibleDefinition()
    {
        var state = new MemoryCollectionState
        {
            CreateException = MemoryIndexFixtures.CommandException(
                68, "IndexAlreadyExists", "Index already exists"),
        };
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue(
            [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 99)]);
        MongoDBMemoryIndexManager manager = CreateManager(state);

        // Unlike EnsureIsIdempotentWhenAConcurrentCallerAlreadyCreatedTheIndex (a compatible rival wins), here the
        // rival concurrent creator won with an *incompatible* definition (different dimensions); Ensure's
        // mandatory post-create re-inspection must still catch this rather than silently accepting the race.
        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(() => manager.EnsureIndexAsync());
    }

    [Fact]
    public async Task EnsureSurfacesPrivilegeErrorTightlyOnCreateFailure()
    {
        var state = new MemoryCollectionState
        {
            CreateException = MemoryIndexFixtures.CommandException(13, "Unauthorized", "not authorized on db"),
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBIndexPrivilegeException>(() => manager.EnsureIndexAsync());
    }

    [Fact]
    public async Task EnsureSurfacesPersistenceErrorForNonPrivilegeFailure()
    {
        var state = new MemoryCollectionState
        {
            CreateException = MemoryIndexFixtures.CommandException(999, "InternalError", "server exploded"),
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBPersistenceException>(() => manager.EnsureIndexAsync());
    }

    [Fact]
    public async Task EnsureUpdatesWhenExistingIndexDoesNotMatchDefinition()
    {
        var state = new MemoryCollectionState();
        state.SearchIndexSnapshots.Enqueue([MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 99)]);
        state.SearchIndexSnapshots.Enqueue([MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)]);
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBIndexInfo info = await manager.EnsureIndexAsync();

        Assert.Equal(1, state.UpdateCallCount);
        Assert.Equal("facade_vector", state.UpdatedIndexName);
        Assert.NotNull(state.UpdatedDefinition);
        Assert.Null(state.CreatedSearchIndex);
        Assert.Equal(MongoDBIndexStatus.Ready, info.Status);
    }

    [Fact]
    public async Task EnsureThrowsMismatchWhenTheIndexStillDoesNotMatchAfterUpdating()
    {
        // The fake update below does not actually change the server-side definition (unlike a real deployment),
        // so the mandatory post-update re-inspection still observes the same mismatched index -- proving Ensure's
        // final validation is not skipped just because an update was attempted.
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 99)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(() => manager.EnsureIndexAsync());

        Assert.Equal(1, state.UpdateCallCount);
        Assert.Null(state.CreatedSearchIndex);
    }

    [Fact]
    public async Task EnsureWithWaitUntilReadyPollsThroughBuildingToReady()
    {
        var state = new MemoryCollectionState();
        BsonDocument building = MemoryIndexFixtures.ValidVectorIndex(
            "facade_vector", "embedding", 3, status: "BUILDING", queryable: false);
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue([building]);
        state.SearchIndexSnapshots.Enqueue([building]);
        state.SearchIndexSnapshots.Enqueue([MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)]);
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBIndexInfo info = await manager.EnsureIndexAsync(
            waitUntilReady: true,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Equal(MongoDBIndexStatus.Ready, info.Status);
    }

    [Fact]
    public async Task WaitUntilReadyThrowsStableTimeoutOnDeadline()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex(
                "facade_vector", "embedding", 3, status: "BUILDING", queryable: false)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        MongoDBTimeoutException exception = await Assert.ThrowsAsync<MongoDBTimeoutException>(
            () => manager.WaitUntilReadyAsync(
                timeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(1)));

        Assert.IsAssignableFrom<MongoDBIndexException>(exception.InnerException);
    }

    [Fact]
    public async Task WaitUntilReadyThrowsFailedExceptionImmediatelyWithoutPolling()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex(
                "facade_vector", "embedding", 3, status: "FAILED", queryable: false)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        // A terminal Failed build never becomes ready on its own, so this must never be retried: exactly one
        // inspection call is made before the actionable, non-transient failure is thrown, regardless of the
        // configured timeout/pollInterval.
        await Assert.ThrowsAsync<MongoDBIndexFailedException>(
            () => manager.WaitUntilReadyAsync(
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(1, state.ListCallCount);
    }

    [Fact]
    public async Task EnsureThrowsFailedExceptionWithoutAutomaticallyRepairingIt()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex(
                "facade_vector", "embedding", 3, status: "FAILED", queryable: false)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        // The Failed index's definition still matches (dimensions=3), so isCompatible is true and Ensure never
        // attempts an update -- a terminal build failure is never something Ensure silently repairs; that must
        // be explicit (recreate/update), matching the state machine.
        await Assert.ThrowsAsync<MongoDBIndexFailedException>(() => manager.EnsureIndexAsync());

        Assert.Equal(0, state.UpdateCallCount);
        Assert.Null(state.CreatedSearchIndex);
    }

    [Fact]
    public async Task WaitUntilReadyPropagatesCancellation()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex(
                "facade_vector", "embedding", 3, status: "BUILDING", queryable: false)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.WaitUntilReadyAsync(
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromSeconds(1),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task UpdateReplacesDefinitionOfAnExistingIndexOnlyWhenExplicitlyCalled()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)],
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await manager.UpdateIndexAsync();

        Assert.Equal("facade_vector", state.UpdatedIndexName);
        Assert.NotNull(state.UpdatedDefinition);
        Assert.Equal(1, state.UpdateCallCount);
    }

    [Fact]
    public async Task UpdateThrowsMissingWhenIndexDoesNotExist()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => manager.UpdateIndexAsync());
        Assert.Equal(0, state.UpdateCallCount);
    }

    [Fact]
    public async Task UpdateSurfacesPrivilegeErrorTightly()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [MemoryIndexFixtures.ValidVectorIndex("facade_vector", "embedding", 3)],
            UpdateException = MemoryIndexFixtures.CommandException(13, "Unauthorized", "not authorized"),
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBIndexPrivilegeException>(() => manager.UpdateIndexAsync());
    }

    [Fact]
    public async Task DropIsIdempotentNoOpWhenAlreadyAbsent()
    {
        var state = new MemoryCollectionState
        {
            DropException = MemoryIndexFixtures.CommandException(27, "IndexNotFound", "index not found"),
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await manager.DropIndexAsync();

        Assert.Equal("facade_vector", state.DroppedIndexName);
        Assert.Equal(1, state.DropOneCallCount);
    }

    [Fact]
    public async Task DropSurfacesPrivilegeErrorTightly()
    {
        var state = new MemoryCollectionState
        {
            DropException = MemoryIndexFixtures.CommandException(13, "Unauthorized", "not authorized"),
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBIndexPrivilegeException>(() => manager.DropIndexAsync());
    }

    [Fact]
    public async Task GetSurfacesPrivilegeErrorDistinctlyFromCapabilityError()
    {
        var state = new MemoryCollectionState
        {
            ListException = MemoryIndexFixtures.CommandException(13, "Unauthorized", "not authorized"),
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBIndexPrivilegeException>(() => manager.GetIndexAsync());
    }

    [Fact]
    public async Task GetSurfacesRetrievalErrorForNonPrivilegeFailure()
    {
        var state = new MemoryCollectionState
        {
            ListException = MemoryIndexFixtures.CommandException(999, "InternalError", "boom"),
        };
        MongoDBMemoryIndexManager manager = CreateManager(state);

        await Assert.ThrowsAsync<MongoDBRetrievalException>(() => manager.GetIndexAsync());
    }

    [Fact]
    public void ConstructorRequiresAtLeastOneOfDefinitionAndCollection()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MongoDBMemoryIndexManager(
                (IMongoCollection<BsonDocument>)null!,
                Definition()));
        Assert.Throws<ArgumentNullException>(
            () => new MongoDBMemoryIndexManager(
                MemoryCollectionProxy.Create(new MemoryCollectionState()),
                null!));
    }

    [Fact]
    public async Task InjectedCollectionRemainsCallerOwned()
    {
        MongoDBMemoryIndexManager manager = CreateManager(new MemoryCollectionState());

        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.False(manager.OwnsClient);
    }

    [Fact]
    public async Task ConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        MongoDBMemoryIndexManager manager = new(
            "mongodb://localhost:27017",
            "database",
            "memories",
            Definition());

        Assert.True(manager.OwnsClient);
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public void ConnectionStringConstructorRejectsEmptyDatabaseName()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => new MongoDBMemoryIndexManager(
                "mongodb://localhost:27017",
                "  ",
                "memories",
                Definition()));
    }

    private static MongoDBMemoryIndexManager CreateManager(MemoryCollectionState state) =>
        new(MemoryCollectionProxy.Create(state), Definition());

    private static MongoDBVectorSearchIndexDefinition Definition() =>
        new("facade_vector", "embedding", 3);
}
