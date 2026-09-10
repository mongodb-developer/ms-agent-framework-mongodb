using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.Memory;

public sealed class MongoDBMemoryIndexAndOwnershipTests
{
    [Fact]
    public async Task EnsureCreatesStructuredVectorDefinitionOnlyWhenExplicitlyCalled()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryProvider provider = CreateProvider(state);

        Assert.Null(state.CreatedSearchIndex);
        string name = await provider.EnsureVectorSearchIndexAsync();

        Assert.Equal("agent_framework_memory", name);
        Assert.Equal("agent_framework_memory", state.CreatedSearchIndex!.Name);
        BsonDocument vector = state.CreatedSearchIndex.Definition["fields"]
            .AsBsonArray[0].AsBsonDocument;
        Assert.Equal(3, vector["numDimensions"]);
        Assert.Equal("content_embedding", vector["path"]);
    }

    [Fact]
    public async Task ValidateRejectsMissingAndMismatchedIndexes()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(
            () => provider.ValidateVectorSearchIndexAsync());
        state.SearchIndexes =
        [
            new BsonDocument
            {
                { "name", "agent_framework_memory" },
                { "status", "READY" },
                { "queryable", true },
                { "latestDefinition", new BsonDocument(
                    "fields",
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            { "type", "vector" }, { "path", "wrong" },
                            { "numDimensions", 3 }, { "similarity", "cosine" },
                        },
                    }) },
            },
        ];

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateVectorSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateRejectsNonVectorSearchIndexType()
    {
        var state = new MemoryCollectionState
        {
            SearchIndexes = [ValidIndex("READY", queryable: true)],
        };
        state.SearchIndexes[0]["type"] = "search";
        MongoDBMemoryProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateVectorSearchIndexAsync());
    }

    [Fact]
    public async Task ReadinessPollingToleratesMissingAndBuildingAfterCreate()
    {
        var state = new MemoryCollectionState();
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue([ValidIndex("BUILDING", queryable: false)]);
        state.SearchIndexSnapshots.Enqueue([ValidIndex("READY", queryable: true)]);
        MongoDBMemoryProvider provider = CreateProvider(state);

        string name = await provider.EnsureVectorSearchIndexAsync(
            waitUntilReady: true,
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Equal("agent_framework_memory", name);
        Assert.NotNull(state.CreatedSearchIndex);
    }

    [Fact]
    public async Task ReadinessDeadlineThrowsStableTimeout()
    {
        var state = new MemoryCollectionState();
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue([]);
        MongoDBMemoryProvider provider = CreateProvider(state);

        MongoDBTimeoutException exception =
            await Assert.ThrowsAsync<MongoDBTimeoutException>(
                () => provider.EnsureVectorSearchIndexAsync(
                    waitUntilReady: true,
                    timeout: TimeSpan.FromMilliseconds(20),
                    pollInterval: TimeSpan.FromMilliseconds(1)));

        Assert.IsAssignableFrom<MongoDBIndexException>(exception.InnerException);
    }

    [Fact]
    public async Task ReadinessPollingPropagatesCancellation()
    {
        var state = new MemoryCollectionState();
        state.SearchIndexSnapshots.Enqueue([]);
        state.SearchIndexSnapshots.Enqueue([ValidIndex("BUILDING", queryable: false)]);
        MongoDBMemoryProvider provider = CreateProvider(state);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.EnsureVectorSearchIndexAsync(
                waitUntilReady: true,
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromSeconds(1),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task InjectedResourcesRemainCallerOwned()
    {
        var embeddings = new RecordingEmbeddingGenerator();
        MongoDBMemoryProvider provider = new(
            MemoryCollectionProxy.Create(new MemoryCollectionState()),
            embeddings,
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope(userId: "user")));

        await provider.DisposeAsync();
        await provider.DisposeAsync();

        Assert.False(provider.OwnsClient);
        Assert.Empty(embeddings.Calls);
    }

    [Fact]
    public async Task ConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        MongoDBMemoryProvider provider = new(
            "mongodb://localhost:27017",
            "database",
            "memories",
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope(userId: "user")));

        Assert.True(provider.OwnsClient);
        await provider.DisposeAsync();
        await provider.DisposeAsync();
    }

    private static MongoDBMemoryProvider CreateProvider(MemoryCollectionState state) =>
        new(
            MemoryCollectionProxy.Create(state),
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope(userId: "user")));

    private static BsonDocument ValidIndex(string status, bool queryable) =>
        new()
        {
            { "name", "agent_framework_memory" },
            { "type", "vectorSearch" },
            { "status", status },
            { "queryable", queryable },
            {
                "latestDefinition",
                new BsonDocument(
                    "fields",
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            { "type", "vector" },
                            { "path", "content_embedding" },
                            { "numDimensions", 3 },
                            { "similarity", "cosine" },
                        },
                        new BsonDocument { { "type", "filter" }, { "path", "application_id" } },
                        new BsonDocument { { "type", "filter" }, { "path", "agent_id" } },
                        new BsonDocument { { "type", "filter" }, { "path", "user_id" } },
                        new BsonDocument { { "type", "filter" }, { "path", "session_id" } },
                    })
            },
        };
}
