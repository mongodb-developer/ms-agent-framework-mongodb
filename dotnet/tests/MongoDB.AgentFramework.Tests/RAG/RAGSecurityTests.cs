using MongoDB.AgentFramework.Internal;
using MongoDB.AgentFramework.Tests.RAG;
using MongoDB.Bson;
using System.Reflection;

namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Consolidated security assertions for the RAG feature, per docs/spec/observability-security.md's mandatory
/// requirements: the authorization-carrying <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/> must land
/// inside every active retrieval branch's own MongoDB Search/Vector Search stage -- before any candidate
/// limiting or <c>$rankFusion</c> combination -- never as a post-hoc, bypassable application-side filter; the
/// public surface must never accept a raw BSON pipeline, filter document, or arbitrary MongoDB operator from a
/// caller (a "model-controlled" surface); every field path and index name is validated before use; and every
/// amplification knob (candidate counts, membership/logical-operand/nesting bounds) is capped so a caller can
/// never force unbounded MongoDB-side work.
/// </summary>
public sealed class RAGSecurityTests
{
    private static readonly float[] QueryVector = [0.1f, 0.2f, 0.3f];

    // -----------------------------------------------------------------------------------------------------
    // Mandatory-filter placement: every mode's retrieval stage carries the filter itself; no mode ever
    // relies on a downstream $match/$limit-independent stage to authorize results.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void VectorAnn_and_Enn_place_the_mandatory_filter_inside_vectorSearch_itself()
    {
        BsonDocument filter = BsonDocument.Parse("""{"tenant_id":"tenant-a"}""");

        foreach (bool exact in new[] { false, true })
        {
            BsonDocument[] stages = RAGPipelineBuilder.BuildVectorSearchPipeline(
                indexName: "vector_index",
                vectorFieldName: "embedding",
                queryVector: QueryVector,
                limit: 5,
                exact: exact,
                numCandidates: exact ? null : 50,
                filter: filter);

            // The filter must be a property of the $vectorSearch stage itself (stage 0) -- MongoDB Search
            // applies it before scoring/limiting any candidate, not after. No later stage exists that could
            // apply it instead.
            BsonDocument vectorSearchStage = Assert.Single(stages, s => s.Contains("$vectorSearch"));
            Assert.Equal(filter, vectorSearchStage["$vectorSearch"]["filter"].AsBsonDocument);
            Assert.Equal(0, Array.IndexOf(stages, vectorSearchStage));
            Assert.DoesNotContain(stages, s => s.Contains("$match"));
        }
    }

    [Fact]
    public void FullText_places_the_mandatory_filter_inside_search_compound_before_the_candidate_limit()
    {
        BsonArray filter = new BsonArray { BsonDocument.Parse("""{"equals":{"path":"tenant_id","value":"tenant-a"}}""") };

        BsonDocument[] stages = RAGPipelineBuilder.BuildFullTextSearchPipeline(
            indexName: "search_index",
            textFieldNames: ["text"],
            queryText: "hello",
            limit: 5,
            filter: filter);

        int searchStageIndex = Array.FindIndex(stages, s => s.Contains("$search"));
        int limitStageIndex = Array.FindIndex(stages, s => s.Contains("$limit"));
        Assert.True(searchStageIndex >= 0 && limitStageIndex >= 0);

        // The filter is authored inside $search.compound.filter -- the retrieval stage itself scores and
        // narrows candidates together, so the filter is applied before the trailing $limit ever runs, and
        // there is no separate $match stage an authorization filter could instead (and less safely) live in.
        Assert.True(searchStageIndex < limitStageIndex);
        BsonDocument compound = stages[searchStageIndex]["$search"]["compound"].AsBsonDocument;
        Assert.Equal(filter, compound["filter"].AsBsonArray);
        Assert.DoesNotContain(stages, s => s.Contains("$match"));
    }

    [Fact]
    public void HybridRrf_places_each_independent_filter_inside_its_own_input_stage_before_rankFusion_and_limit()
    {
        BsonDocument vectorFilter = BsonDocument.Parse("""{"tenant_id":"tenant-a"}""");
        BsonArray searchFilter = new BsonArray { BsonDocument.Parse("""{"equals":{"path":"tenant_id","value":"tenant-a"}}""") };

        BsonDocument[] stages = RAGPipelineBuilder.BuildHybridRankFusionPipeline(
            vectorIndexName: "vector_index",
            vectorFieldName: "embedding",
            queryVector: QueryVector,
            vectorNumCandidates: 50,
            vectorCandidateLimit: 50,
            vectorFilter: vectorFilter,
            searchIndexName: "search_index",
            textFieldNames: ["text"],
            queryText: "hello",
            textCandidateLimit: 50,
            searchFilter: searchFilter,
            vectorWeight: 1.0,
            textWeight: 1.0,
            includeScoreDetails: false,
            limit: 5);

        // $rankFusion is always the first stage (both retrieval branches run as its own sub-pipelines); the
        // final $limit (topK) comes strictly after it. Both sub-pipeline filters must already be embedded
        // inside $rankFusion's own input definitions -- there is no opportunity for an unauthorized candidate
        // to ever reach the fused, limited result set, and no separate $match stage exists anywhere.
        BsonDocument rankFusionStage = Assert.Single(stages, s => s.Contains("$rankFusion"));
        Assert.Equal(0, Array.IndexOf(stages, rankFusionStage));
        int limitStageIndex = Array.FindIndex(stages, s => s.Contains("$limit"));
        Assert.True(limitStageIndex > 0);
        Assert.DoesNotContain(stages, s => s.Contains("$match"));

        BsonDocument pipelines = rankFusionStage["$rankFusion"]["input"]["pipelines"].AsBsonDocument;
        BsonDocument vectorInputStage = pipelines["vector"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal(vectorFilter, vectorInputStage["$vectorSearch"]["filter"].AsBsonDocument);

        BsonArray textInputPipeline = pipelines["text"].AsBsonArray;
        BsonDocument textSearchSubStage = Assert.Single(textInputPipeline, s => s.AsBsonDocument.Contains("$search"))
            .AsBsonDocument;
        int textSearchIndex = textInputPipeline.IndexOf(textSearchSubStage);
        int textLimitIndex = textInputPipeline.ToList().FindIndex(s => s.AsBsonDocument.Contains("$limit"));

        // Within the text input's own sub-pipeline, its filter (inside $search.compound.filter) still precedes
        // that input's own candidate $limit, exactly mirroring the standalone FullText mode's ordering.
        Assert.True(textSearchIndex < textLimitIndex);
        BsonDocument textCompound = textSearchSubStage["$search"]["compound"].AsBsonDocument;
        Assert.Equal(searchFilter, textCompound["filter"].AsBsonArray);
    }

    [Fact]
    public void No_pipeline_mode_ever_omits_the_configured_mandatory_filter_when_one_is_supplied()
    {
        // A defense-in-depth check that every one of the three pipeline-building entry points requires an
        // explicit filter argument (there is no overload lacking one) -- a caller of RAGPipelineBuilder cannot
        // accidentally build a pipeline that silently drops a configured, translated filter.
        MethodInfo[] builders =
        [
            typeof(RAGPipelineBuilder).GetMethod(nameof(RAGPipelineBuilder.BuildVectorSearchPipeline))!,
            typeof(RAGPipelineBuilder).GetMethod(nameof(RAGPipelineBuilder.BuildFullTextSearchPipeline))!,
            typeof(RAGPipelineBuilder).GetMethod(nameof(RAGPipelineBuilder.BuildHybridRankFusionPipeline))!,
        ];

        foreach (MethodInfo builder in builders)
        {
            Assert.Contains(
                builder.GetParameters(),
                p => p.Name is "filter" or "vectorFilter" or "searchFilter");
        }
    }

    // -----------------------------------------------------------------------------------------------------
    // Rejecting raw BSON / model-controlled surfaces: the public API never accepts an arbitrary pipeline,
    // filter document, or MongoDB operator supplied at query time (e.g. by a model/tool call).
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void SearchAsync_public_overloads_accept_only_a_plain_query_string_and_cancellation_token()
    {
        MethodInfo[] searchOverloads = typeof(MongoDBRAGProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(MongoDBRAGProvider.SearchAsync))
            .ToArray();

        Assert.NotEmpty(searchOverloads);
        foreach (MethodInfo overload in searchOverloads)
        {
            ParameterInfo[] parameters = overload.GetParameters();

            // Every parameter must be either the free-text query (string) or a CancellationToken -- never a
            // BsonDocument/BsonArray/pipeline/filter type a caller (or a model driving a tool call) could use
            // to inject arbitrary MongoDB operators, field names, or stages.
            Assert.All(
                parameters,
                p => Assert.True(
                    p.ParameterType == typeof(string) || p.ParameterType == typeof(CancellationToken),
                    $"Unexpected SearchAsync parameter '{p.Name}' of type {p.ParameterType}."));
        }
    }

    [Fact]
    public void MongoDBRAGFilter_exposes_no_public_constructor_or_raw_BSON_producing_factory()
    {
        // The filter AST is a closed hierarchy: every subtype's constructor is internal, and every public
        // factory method accepts only typed field paths/values/operands -- never a BsonDocument, BsonArray,
        // or a raw pipeline-stage string a caller could use to smuggle an arbitrary operator or field
        // reference into a query MongoDB itself will execute.
        Assert.Empty(typeof(MongoDBRAGFilter).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        MethodInfo[] factories = typeof(MongoDBRAGFilter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(MongoDBRAGFilter))
            .ToArray();
        Assert.NotEmpty(factories);
        foreach (MethodInfo factory in factories)
        {
            Assert.DoesNotContain(
                factory.GetParameters(),
                p => p.ParameterType == typeof(BsonDocument) || p.ParameterType == typeof(BsonArray));
        }
    }

    [Fact]
    public void MongoDBRAGProviderOptions_exposes_no_raw_BSON_pipeline_or_filter_document_property()
    {
        // Options are the only place authorization/query shaping is configured; none of its settable
        // properties may accept a raw BsonDocument/BsonArray (an escape hatch that would let a caller bypass
        // the typed, bounded MongoDBRAGFilter AST and field-path validation).
        PropertyInfo[] properties = typeof(MongoDBRAGProviderOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(
            properties,
            p => p.PropertyType == typeof(BsonDocument) || p.PropertyType == typeof(BsonArray));
    }

    // -----------------------------------------------------------------------------------------------------
    // Field/index validation: invalid field paths and index names are rejected before any MongoDB contact.
    // -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("$where")]
    [InlineData("a.$b")]
    [InlineData(".leadingDot")]
    [InlineData("trailingDot.")]
    public void MongoDBRAGFilter_rejects_invalid_or_operator_injecting_field_paths(string fieldPath)
    {
        Assert.Throws<MongoDBConfigurationException>(() => MongoDBRAGFilter.Equal(fieldPath, "value"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MongoDBRAGProviderOptions_rejects_invalid_index_names(string indexName)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorIndexName = indexName,
        };
        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$injected")]
    public void MongoDBRAGProviderOptions_rejects_invalid_configured_field_paths(string fieldPath)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorFieldName = fieldPath,
        };
        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    // -----------------------------------------------------------------------------------------------------
    // Bounded amplification: every caller-influenced candidate/operand/nesting count is capped, so no caller
    // can force MongoDB to score, fetch, fuse, or filter an unbounded number of candidates.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TopK_is_rejected_above_its_maximum()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            TopK = MongoDBRAGProviderOptions.MaxTopK + 1,
        };
        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void NumCandidates_is_rejected_above_its_maximum()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            NumCandidates = MongoDBRAGProviderOptions.MaxNumCandidates + 1,
        };
        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(nameof(MongoDBRAGProviderOptions.VectorCandidateLimit))]
    [InlineData(nameof(MongoDBRAGProviderOptions.TextCandidateLimit))]
    public void HybridCandidateLimits_are_each_independently_bounded_by_the_same_maximum(string propertyName)
    {
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.HybridRrf };
        typeof(MongoDBRAGProviderOptions).GetProperty(propertyName)!.SetValue(
            options, MongoDBRAGProviderOptions.MaxNumCandidates + 1);

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void MembershipFilter_is_rejected_above_its_maximum_value_count()
    {
        object[] tooMany = [.. Enumerable.Range(0, MongoDBRAGFilter.MaxMembershipValues + 1).Select(i => (object)i)];
        Assert.Throws<MongoDBConfigurationException>(() => MongoDBRAGFilter.In("field", tooMany));
    }

    [Fact]
    public void LogicalFilter_is_rejected_above_its_maximum_operand_count()
    {
        MongoDBRAGFilter[] tooMany =
        [
            .. Enumerable.Range(0, MongoDBRAGFilter.MaxLogicalOperands + 1)
                .Select(i => MongoDBRAGFilter.Equal("field", i)),
        ];
        Assert.Throws<MongoDBConfigurationException>(() => MongoDBRAGFilter.And(tooMany));
    }

    [Fact]
    public void LogicalFilter_nesting_is_rejected_above_its_maximum_depth()
    {
        MongoDBRAGFilter current = MongoDBRAGFilter.Equal("field", 1);
        Assert.Throws<MongoDBConfigurationException>(() =>
        {
            for (int depth = 0; depth <= MongoDBRAGFilter.MaxNestingDepth + 1; depth++)
            {
                current = MongoDBRAGFilter.And(current, MongoDBRAGFilter.Equal("field", depth));
            }
        });
    }

    [Fact]
    public async Task SearchAsync_never_issues_more_than_one_aggregate_call_per_retrieval_regardless_of_mode()
    {
        // A caller cannot amplify the number of round trips a single SearchAsync call makes to MongoDB: each
        // mode (including Hybrid, which combines two logical retrieval branches) must still issue exactly one
        // aggregate command, never one per branch.
        foreach (MongoDBSearchMode mode in new[]
        {
            MongoDBSearchMode.VectorAnn, MongoDBSearchMode.VectorEnn, MongoDBSearchMode.HybridRrf,
        })
        {
            var state = new RAGCollectionState
            {
                Results = [],
                SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
            };
            var options = new MongoDBRAGProviderOptions { SearchMode = mode };
            var provider = new MongoDBRAGProvider(
                RAGCollectionProxy.Create(state),
                new RecordingEmbeddingGenerator(),
                3,
                options);

            await provider.SearchAsync("query");

            int aggregateCallCount = state.AggregateStages.Count(s => s.Contains("$vectorSearch") || s.Contains("$search") || s.Contains("$rankFusion"));
            Assert.Equal(1, aggregateCallCount);
        }
    }
}
