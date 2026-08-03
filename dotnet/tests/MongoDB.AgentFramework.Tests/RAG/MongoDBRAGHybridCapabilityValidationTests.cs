using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;

namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Exercises <see cref="MongoDBRAGProvider.ValidateHybridSearchCapabilityAsync"/>, the read-only
/// server-version/index capability seam for <see cref="MongoDBSearchMode.HybridRrf"/>'s <c>$rankFusion</c> stage
/// (rag.md's Hybrid capability matrix row; MongoDB 8.0+ is required for <c>$rankFusion</c>). Mirrors
/// <c>MongoDBRAGSearchIndexValidationTests</c>'s conventions, extended to also validate the Vector Search index
/// used by Hybrid's vector input branch and the server's <c>buildInfo</c> version.
/// </summary>
public sealed class MongoDBRAGHybridCapabilityValidationTests
{
    [Fact]
    public async Task ValidateRejectsAServerOlderThanEight()
    {
        var state = new RAGCollectionState
        {
            BuildInfoResult = new BsonDocument("version", "7.0.9"),
            SearchIndexes = [ValidVectorIndex(), ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        MongoDBCapabilityException exception = await Assert.ThrowsAsync<MongoDBCapabilityException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("8.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAcceptsAServerAtExactlyEight()
    {
        var state = new RAGCollectionState
        {
            BuildInfoResult = new BsonDocument("version", "8.0.0"),
            SearchIndexes = [ValidVectorIndex(), ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.ValidateHybridSearchCapabilityAsync();
    }

    [Fact]
    public async Task ValidateRejectsAnUnparsableServerVersionWithAnActionableError()
    {
        var state = new RAGCollectionState
        {
            BuildInfoResult = new BsonDocument("version", "not-a-version"),
            SearchIndexes = [ValidVectorIndex(), ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        MongoDBCapabilityException exception = await Assert.ThrowsAsync<MongoDBCapabilityException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("8.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateWrapsABuildInfoFailureAsACapabilityError()
    {
        var state = new RAGCollectionState
        {
            RunCommandException = new MongoConnectionException(
                new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                "offline"),
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBCapabilityException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidatePropagatesCancellationFromBuildInfoRatherThanWrappingIt()
    {
        var state = new RAGCollectionState
        {
            RunCommandException = new OperationCanceledException(),
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsAMissingVectorSearchIndex()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsAMissingSearchIndex()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidVectorIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsAVectorIndexWithTheWrongType()
    {
        BsonDocument vectorIndex = ValidVectorIndex();
        vectorIndex["type"] = "search";
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsAVectorIndexWithAMismatchedDimension()
    {
        BsonDocument vectorIndex = ValidVectorIndex();
        vectorIndex["latestDefinition"]["fields"].AsBsonArray[0]["numDimensions"] = 99;
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsAVectorIndexMissingTheConfiguredField()
    {
        BsonDocument vectorIndex = ValidVectorIndex();
        vectorIndex["latestDefinition"]["fields"].AsBsonArray[0]["path"] = "other_field";
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsANotReadyVectorIndexWhenReadyIsRequired()
    {
        BsonDocument vectorIndex = ValidVectorIndex();
        vectorIndex["status"] = "BUILDING";
        vectorIndex["queryable"] = false;
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsANotReadySearchIndexWhenReadyIsRequired()
    {
        BsonDocument searchIndex = ValidSearchIndex();
        searchIndex["status"] = "BUILDING";
        searchIndex["queryable"] = false;
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidVectorIndex(), searchIndex],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateAllowsNotReadyIndexesWhenReadyIsNotRequired()
    {
        BsonDocument vectorIndex = ValidVectorIndex();
        vectorIndex["status"] = "BUILDING";
        vectorIndex["queryable"] = false;
        BsonDocument searchIndex = ValidSearchIndex();
        searchIndex["status"] = "BUILDING";
        searchIndex["queryable"] = false;
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, searchIndex],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.ValidateHybridSearchCapabilityAsync(requireReady: false);
    }

    [Fact]
    public async Task ValidateAcceptsBothValidIndexesOnAServerAtLeastEight()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidVectorIndex(), ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.ValidateHybridSearchCapabilityAsync();
    }

    [Fact]
    public async Task ValidateOnlyAppliesToHybridMode()
    {
        var state = new RAGCollectionState();
        MongoDBRAGProvider provider = new(
            RAGCollectionProxy.Create(state),
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn });

        await Assert.ThrowsAsync<MongoDBCapabilityException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Equal(0, state.RunCommandCallCount);
        Assert.Equal(0, state.SearchIndexListCallCount);
    }

    [Fact]
    public async Task ValidateReusesACachedResultWithinTheBoundedInterval()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidVectorIndex(), ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);
        var clock = new FakeTimeProvider();
        provider.TimeProvider = clock;

        await provider.ValidateHybridSearchCapabilityAsync();
        Assert.Equal(1, state.RunCommandCallCount);

        clock.UtcNow += TimeSpan.FromSeconds(1);
        await provider.ValidateHybridSearchCapabilityAsync();

        Assert.Equal(1, state.RunCommandCallCount);
    }

    [Fact]
    public async Task ValidateRefreshBypassesTheCache()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidVectorIndex(), ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.ValidateHybridSearchCapabilityAsync();
        await provider.ValidateHybridSearchCapabilityAsync(refresh: true);

        Assert.Equal(2, state.RunCommandCallCount);
    }

    [Fact]
    public async Task ValidateExpiresTheCacheAfterTheBoundedInterval()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidVectorIndex(), ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);
        var clock = new FakeTimeProvider();
        provider.TimeProvider = clock;

        await provider.ValidateHybridSearchCapabilityAsync();
        clock.UtcNow += TimeSpan.FromMinutes(10);
        await provider.ValidateHybridSearchCapabilityAsync();

        Assert.Equal(2, state.RunCommandCallCount);
    }

    [Fact]
    public async Task ValidateDoesNotServeAStaleNotReadyCacheWhenReadinessIsLaterRequired()
    {
        BsonDocument vectorIndex = ValidVectorIndex();
        vectorIndex["status"] = "BUILDING";
        vectorIndex["queryable"] = false;
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.ValidateHybridSearchCapabilityAsync(requireReady: false);
        Assert.Equal(1, state.RunCommandCallCount);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(
            () => provider.ValidateHybridSearchCapabilityAsync(requireReady: true));
        Assert.Equal(2, state.RunCommandCallCount);
    }

    private static MongoDBRAGProvider CreateProvider(RAGCollectionState state) =>
        new(
            RAGCollectionProxy.Create(state),
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions
            {
                SearchMode = MongoDBSearchMode.HybridRrf,
                VectorIndexName = "agent_framework_rag_vector",
                VectorFieldName = "embedding",
                SearchIndexName = "agent_framework_rag_search",
                SearchTextFieldNames = ["text"],
            });

    private static BsonDocument ValidVectorIndex() =>
        new()
        {
            { "name", "agent_framework_rag_vector" },
            { "type", "vectorSearch" },
            { "status", "READY" },
            { "queryable", true },
            {
                "latestDefinition",
                new BsonDocument(
                    "fields",
                    new BsonArray
                    {
                        new BsonDocument
                        {
                            { "type", "vector" },
                            { "path", "embedding" },
                            { "numDimensions", 3 },
                            { "similarity", "cosine" },
                        },
                    })
            },
        };

    private static BsonDocument ValidSearchIndex() =>
        new()
        {
            { "name", "agent_framework_rag_search" },
            { "type", "search" },
            { "status", "READY" },
            { "queryable", true },
            {
                "latestDefinition",
                new BsonDocument(
                    "mappings",
                    new BsonDocument
                    {
                        { "dynamic", false },
                        { "fields", new BsonDocument
                            {
                                { "text", new BsonDocument("type", "string") },
                            }
                        },
                    })
            },
        };
}
