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
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
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
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
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
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
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
            SearchIndexes = [RAGIndexFixtures.ValidSearchIndex()],
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
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsAVectorIndexWithTheWrongType()
    {
        BsonDocument vectorIndex = RAGIndexFixtures.ValidVectorIndex();
        vectorIndex["type"] = "search";
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, RAGIndexFixtures.ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsAVectorIndexWithAMismatchedDimension()
    {
        BsonDocument vectorIndex = RAGIndexFixtures.ValidVectorIndex();
        vectorIndex["latestDefinition"]["fields"].AsBsonArray[0]["numDimensions"] = 99;
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, RAGIndexFixtures.ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsAVectorIndexMissingTheConfiguredField()
    {
        BsonDocument vectorIndex = RAGIndexFixtures.ValidVectorIndex();
        vectorIndex["latestDefinition"]["fields"].AsBsonArray[0]["path"] = "other_field";
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, RAGIndexFixtures.ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsANotReadyVectorIndexWhenReadyIsRequired()
    {
        BsonDocument vectorIndex = RAGIndexFixtures.ValidVectorIndex();
        vectorIndex["status"] = "BUILDING";
        vectorIndex["queryable"] = false;
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, RAGIndexFixtures.ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateRejectsANotReadySearchIndexWhenReadyIsRequired()
    {
        BsonDocument searchIndex = RAGIndexFixtures.ValidSearchIndex();
        searchIndex["status"] = "BUILDING";
        searchIndex["queryable"] = false;
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), searchIndex],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateAllowsNotReadyIndexesWhenReadyIsNotRequired()
    {
        BsonDocument vectorIndex = RAGIndexFixtures.ValidVectorIndex();
        vectorIndex["status"] = "BUILDING";
        vectorIndex["queryable"] = false;
        BsonDocument searchIndex = RAGIndexFixtures.ValidSearchIndex();
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
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
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
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
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
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
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
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
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
        BsonDocument vectorIndex = RAGIndexFixtures.ValidVectorIndex();
        vectorIndex["status"] = "BUILDING";
        vectorIndex["queryable"] = false;
        var state = new RAGCollectionState
        {
            SearchIndexes = [vectorIndex, RAGIndexFixtures.ValidSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.ValidateHybridSearchCapabilityAsync(requireReady: false);
        Assert.Equal(1, state.RunCommandCallCount);

        await Assert.ThrowsAsync<MongoDBIndexNotReadyException>(
            () => provider.ValidateHybridSearchCapabilityAsync(requireReady: true));
        Assert.Equal(2, state.RunCommandCallCount);
    }

    [Fact]
    public async Task ValidateRejectsAMandatoryFilterFieldNotIndexedAsAVectorSearchFilterField()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["tenant_id"] = "token",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("tenant_id", "acme"));

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("tenant_id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("filter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateRejectsAMandatoryFilterFieldIndexedAsTheWrongVectorSearchFieldType()
    {
        BsonDocument vectorIndex = RAGIndexFixtures.ValidVectorIndex();
        vectorIndex["latestDefinition"].AsBsonDocument["fields"].AsBsonArray.Add(
            new BsonDocument { { "type", "token" }, { "path", "tenant_id" } });
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                vectorIndex,
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["tenant_id"] = "token",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("tenant_id", "acme"));

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
    }

    [Fact]
    public async Task ValidateAcceptsAMandatoryFilterFieldIndexedAsAVectorSearchFilterField()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["tenant_id"] = "token",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("tenant_id", "acme"));

        await provider.ValidateHybridSearchCapabilityAsync();
    }

    [Fact]
    public async Task ValidateRejectsAMandatoryFilterFieldNotMappedInANonDynamicSearchIndex()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id"]),
                RAGIndexFixtures.ValidSearchIndex(),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("tenant_id", "acme"));

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("tenant_id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateRejectsAMandatoryRangeFilterFieldMappedToAnIncompatibleSearchType()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["published_at"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["published_at"] = "token",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(
            state, MongoDBRAGFilter.Range("published_at", minimum: 0, maximum: null));

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("published_at", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAcceptsAMandatoryRangeFilterFieldMappedToANumberSearchType()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["published_at"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["published_at"] = "number",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(
            state, MongoDBRAGFilter.Range("published_at", minimum: 0, maximum: null));

        await provider.ValidateHybridSearchCapabilityAsync();
    }

    [Fact]
    public async Task ValidateRejectsAStringEqualityValueMappedAsSearchStringRatherThanToken()
    {
        // Atlas Search "string" fields are full-text analyzed, not exact-match compatible; only "token" supports
        // equality/membership filtering.
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["tenant_id"] = "string",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("tenant_id", "acme"));

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("tenant_id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAcceptsAStringEqualityValueMappedAsSearchToken()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["tenant_id"] = "token",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("tenant_id", "acme"));

        await provider.ValidateHybridSearchCapabilityAsync();
    }

    [Fact]
    public async Task ValidateRejectsANumericEqualityValueMappedOnlyAsSearchToken()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["priority"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["priority"] = "token",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("priority", 1));

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("priority", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateRejectsAHeterogeneousMembershipFilterAgainstASingleTypeSearchMapping()
    {
        // The "in" list mixes a string and a numeric value; a field mapped only to "token" satisfies the string
        // category but not the numeric one, so this must be rejected rather than accepted because at least one
        // value category matched.
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["mixed_id"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["mixed_id"] = "token",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.In("mixed_id", ["acme", 42]));

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("mixed_id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAcceptsAHeterogeneousMembershipFilterAgainstAMultiTypeSearchMappingArray()
    {
        // A single field path may have multiple Atlas Search mapping definitions (one per type); the union of
        // definitions must cover every referenced value category, even though no single definition covers both.
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["mixed_id"]),
                RAGIndexFixtures.ValidSearchIndex(multiTypeFilterFieldTypes: new Dictionary<string, string[]>
                {
                    ["mixed_id"] = ["token", "number"],
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.In("mixed_id", ["acme", 42]));

        await provider.ValidateHybridSearchCapabilityAsync();
    }

    [Fact]
    public async Task ValidateRejectsMismatchedValueCategoriesAcrossNestedAndOrOperands()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", "acme"),
            MongoDBRAGFilter.Or(
                MongoDBRAGFilter.Equal("priority", 1),
                MongoDBRAGFilter.Range("published_at", minimum: 0, maximum: null)));
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id", "priority", "published_at"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["tenant_id"] = "token",
                    // "priority" is mapped only as "token", which is not compatible with its numeric equality
                    // value even though the field itself is mapped -- only reachable through the nested Or
                    // operand, exercising both nested recursion and value-category checking together.
                    ["priority"] = "token",
                    ["published_at"] = "number",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, filter);

        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("priority", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateClearsAPriorCachedSuccessWhenRefreshFindsAnUnverifiableDynamicMapping()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["tenant_id"] = "token",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("tenant_id", "acme"));
        var clock = new FakeTimeProvider();
        provider.TimeProvider = clock;

        // First call: the mapping is statically verified, so the result is cached.
        await provider.ValidateHybridSearchCapabilityAsync();
        Assert.Equal(1, state.RunCommandCallCount);
        await provider.ValidateHybridSearchCapabilityAsync();
        Assert.Equal(1, state.RunCommandCallCount);

        // The Search index's mapping later becomes dynamic (unverifiable); a forced refresh discovers this.
        state.SearchIndexes =
        [
            RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id"]),
            RAGIndexFixtures.DynamicSearchIndex(),
        ];
        await provider.ValidateHybridSearchCapabilityAsync(refresh: true);
        Assert.Equal(2, state.RunCommandCallCount);

        // The stale cached success from before the mapping became dynamic must have been explicitly cleared: the
        // very next plain (non-refresh) call must re-validate rather than short-circuiting on it.
        await provider.ValidateHybridSearchCapabilityAsync();
        Assert.Equal(3, state.RunCommandCallCount);
    }

    [Fact]
    public async Task ValidateChecksEveryFieldReferencedAcrossNestedAndOrOperands()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", "acme"),
            MongoDBRAGFilter.Or(
                MongoDBRAGFilter.In("category", ["docs", "faq"]),
                MongoDBRAGFilter.Range("published_at", minimum: 0, maximum: null)));
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id", "category"]),
                RAGIndexFixtures.ValidSearchIndex(filterFieldTypes: new Dictionary<string, string>
                {
                    ["tenant_id"] = "token",
                    ["category"] = "token",
                    ["published_at"] = "number",
                }),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, filter);

        // "published_at" is not indexed as a Vector Search filter field, so this must still be rejected even
        // though it is only reachable through the nested Or operand.
        MongoDBIndexMismatchException exception = await Assert.ThrowsAsync<MongoDBIndexMismatchException>(
            () => provider.ValidateHybridSearchCapabilityAsync());
        Assert.Contains("published_at", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateDoesNotCacheSuccessWhenTheSearchMappingIsDynamicAndFilterFieldsAreUnverified()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes =
            [
                RAGIndexFixtures.ValidVectorIndex(filterFieldPaths: ["tenant_id"]),
                RAGIndexFixtures.DynamicSearchIndex(),
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state, MongoDBRAGFilter.Equal("tenant_id", "acme"));
        var clock = new FakeTimeProvider();
        provider.TimeProvider = clock;

        await provider.ValidateHybridSearchCapabilityAsync();
        Assert.Equal(1, state.RunCommandCallCount);

        await provider.ValidateHybridSearchCapabilityAsync();

        Assert.Equal(2, state.RunCommandCallCount);
    }

    [Fact]
    public async Task ValidateStillCachesWhenTheSearchMappingIsDynamicAndThereIsNoMandatoryFilter()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.DynamicSearchIndex()],
        };
        MongoDBRAGProvider provider = CreateProvider(state);
        var clock = new FakeTimeProvider();
        provider.TimeProvider = clock;

        await provider.ValidateHybridSearchCapabilityAsync();
        Assert.Equal(1, state.RunCommandCallCount);

        await provider.ValidateHybridSearchCapabilityAsync();

        Assert.Equal(1, state.RunCommandCallCount);
    }

    private static MongoDBRAGProvider CreateProvider(
        RAGCollectionState state, MongoDBRAGFilter mandatoryFilter) =>
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
                MandatoryFilter = mandatoryFilter,
            });

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
}
