using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;

namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Exercises <see cref="MongoDBRAGProvider.ValidateSearchIndexAsync"/>, the read-only Search-index
/// capability/validation seam for <see cref="MongoDBSearchMode.FullText"/> (rag.md 291-314). Mirrors
/// <c>MongoDBMemoryIndexAndOwnershipTests</c>'s conventions, adapted for the Atlas Search
/// <c>mappings.dynamic</c>/<c>mappings.fields</c> definition shape instead of Vector Search's flat <c>fields</c>
/// array.
/// </summary>
public sealed class MongoDBRAGSearchIndexValidationTests
{
    [Fact]
    public async Task ValidateRejectsMissingIndex()
    {
        var state = new RAGCollectionState();
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(
            () => provider.ValidateSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateRejectsNonSearchIndexType()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidIndex("READY", queryable: true)],
        };
        state.SearchIndexes[0]["type"] = "vectorSearch";
        MongoDBRAGProvider provider = CreateProvider(state);

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateSearchIndexAsync());
        Assert.Contains("agent_framework_rag_search", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateRejectsIndexMissingAConfiguredTextField()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                new BsonDocument
                {
                    { "name", "agent_framework_rag_search" },
                    { "type", "search" },
                    { "status", "READY" },
                    { "queryable", true },
                    { "latestDefinition", new BsonDocument(
                        "mappings",
                        new BsonDocument
                        {
                            { "dynamic", false },
                            { "fields", new BsonDocument
                                {
                                    { "other", new BsonDocument("type", "string") },
                                }
                            },
                        }) },
                },
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateRejectsAConfiguredFieldMappedToANonTextType()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                new BsonDocument
                {
                    { "name", "agent_framework_rag_search" },
                    { "type", "search" },
                    { "status", "READY" },
                    { "queryable", true },
                    { "latestDefinition", new BsonDocument(
                        "mappings",
                        new BsonDocument
                        {
                            { "dynamic", false },
                            { "fields", new BsonDocument
                                {
                                    { "text", new BsonDocument("type", "number") },
                                }
                            },
                        }) },
                },
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateAcceptsANestedConfiguredTextFieldPath()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                new BsonDocument
                {
                    { "name", "agent_framework_rag_search" },
                    { "type", "search" },
                    { "status", "READY" },
                    { "queryable", true },
                    { "latestDefinition", new BsonDocument(
                        "mappings",
                        new BsonDocument
                        {
                            { "dynamic", false },
                            { "fields", new BsonDocument
                                {
                                    {
                                        "chunk", new BsonDocument
                                        {
                                            { "type", "document" },
                                            { "fields", new BsonDocument
                                                {
                                                    { "text", new BsonDocument("type", "string") },
                                                }
                                            },
                                        }
                                    },
                                }
                            },
                        }) },
                },
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, textField: "chunk.text");

        await provider.ValidateSearchIndexAsync();
    }

    [Fact]
    public async Task ValidateSkipsFieldEnumerationForADynamicMapping()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                new BsonDocument
                {
                    { "name", "agent_framework_rag_search" },
                    { "type", "search" },
                    { "status", "READY" },
                    { "queryable", true },
                    { "latestDefinition", new BsonDocument(
                        "mappings",
                        new BsonDocument("dynamic", true)) },
                },
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        // A dynamic mapping indexes every field automatically, so listSearchIndexes provides no per-field
        // enumeration to check against; this is a documented limitation, not a failure to validate.
        await provider.ValidateSearchIndexAsync();
    }

    [Fact]
    public async Task ValidateRejectsANotReadyIndexWhenReadyIsRequired()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidIndex("BUILDING", queryable: false)],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(
            () => provider.ValidateSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateAllowsANotReadyIndexWhenReadyIsNotRequired()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidIndex("BUILDING", queryable: false)],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.ValidateSearchIndexAsync(requireReady: false);
    }

    [Fact]
    public async Task ValidateOnlyAppliesToFullTextMode()
    {
        var state = new RAGCollectionState();
        MongoDBRAGProvider provider = new(
            RAGCollectionProxy.Create(state),
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn });

        await Assert.ThrowsAsync<MongoDBCapabilityException>(
            () => provider.ValidateSearchIndexAsync());
        Assert.Equal(0, state.SearchIndexListCallCount);
    }

    [Fact]
    public async Task ValidatePropagatesCancellationRatherThanWrappingIt()
    {
        var state = new RAGCollectionState
        {
            SearchIndexListException = new OperationCanceledException(),
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ValidateSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateWrapsAListingFailureAsACapabilityError()
    {
        var state = new RAGCollectionState
        {
            SearchIndexListException = new MongoConnectionException(
                new ConnectionId(
                    new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                "offline"),
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBCapabilityException>(
            () => provider.ValidateSearchIndexAsync());
    }

    [Fact]
    public async Task ValidateReusesACachedResultWithinTheBoundedInterval()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidIndex("READY", queryable: true)],
        };
        MongoDBRAGProvider provider = CreateProvider(state);
        var clock = new FakeTimeProvider();
        provider.TimeProvider = clock;

        await provider.ValidateSearchIndexAsync();
        Assert.Equal(1, state.SearchIndexListCallCount);

        clock.UtcNow += TimeSpan.FromSeconds(1);
        await provider.ValidateSearchIndexAsync();

        Assert.Equal(1, state.SearchIndexListCallCount);
    }

    [Fact]
    public async Task ValidateRefreshBypassesTheCache()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidIndex("READY", queryable: true)],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.ValidateSearchIndexAsync();
        await provider.ValidateSearchIndexAsync(refresh: true);

        Assert.Equal(2, state.SearchIndexListCallCount);
    }

    [Fact]
    public async Task ValidateExpiresTheCacheAfterTheBoundedInterval()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidIndex("READY", queryable: true)],
        };
        MongoDBRAGProvider provider = CreateProvider(state);
        var clock = new FakeTimeProvider();
        provider.TimeProvider = clock;

        await provider.ValidateSearchIndexAsync();
        clock.UtcNow += TimeSpan.FromMinutes(10);
        await provider.ValidateSearchIndexAsync();

        Assert.Equal(2, state.SearchIndexListCallCount);
    }

    [Fact]
    public async Task ValidateDoesNotServeAStaleNotReadyCacheWhenReadinessIsLaterRequired()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [ValidIndex("BUILDING", queryable: false)],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        // A cached "not required to be ready" validation must not silently satisfy a later call that does require
        // readiness -- otherwise a caller could observe a stale non-ready index as validated.
        await provider.ValidateSearchIndexAsync(requireReady: false);
        Assert.Equal(1, state.SearchIndexListCallCount);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(
            () => provider.ValidateSearchIndexAsync(requireReady: true));
        Assert.Equal(2, state.SearchIndexListCallCount);
    }

    private static MongoDBRAGProvider CreateProvider(RAGCollectionState state, string textField = "text") =>
        new(
            RAGCollectionProxy.Create(state),
            new MongoDBRAGProviderOptions
            {
                SearchMode = MongoDBSearchMode.FullText,
                SearchIndexName = "agent_framework_rag_search",
                SearchTextFieldNames = [textField],
            });

    private static BsonDocument ValidIndex(string status, bool queryable) =>
        new()
        {
            { "name", "agent_framework_rag_search" },
            { "type", "search" },
            { "status", status },
            { "queryable", queryable },
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
