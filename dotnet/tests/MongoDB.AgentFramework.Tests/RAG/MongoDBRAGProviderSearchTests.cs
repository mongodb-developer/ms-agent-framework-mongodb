using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;

namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class MongoDBRAGProviderSearchTests
{
    [Theory]
    [InlineData(false, "numCandidates")]
    [InlineData(true, "exact")]
    public async Task SearchPlacesMandatoryFilterInsideTheVectorSearchStage(bool exact, string option)
    {
        var state = new RAGCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "chunk-1" },
                    { "text", "Example chunk." },
                    { "_ragScore", 0.87 },
                    { "source", new BsonDocument { { "name", "Doc" }, { "url", "https://example.test" } } },
                    { "category", "docs" },
                },
            ],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = exact ? MongoDBSearchMode.VectorEnn : MongoDBSearchMode.VectorAnn,
            MetadataFieldNames = ["category"],
            MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
        };
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        IReadOnlyList<MongoDBRAGResult> results = await provider.SearchAsync("blue widgets");

        BsonDocument vectorSearch = state.AggregateStages[0]["$vectorSearch"].AsBsonDocument;
        Assert.True(vectorSearch.Contains(option));
        Assert.Equal(
            BsonDocument.Parse("""{"tenant_id":{"$eq":"tenant-a"}}"""),
            vectorSearch["filter"].AsBsonDocument);
        MongoDBRAGResult result = Assert.Single(results);
        Assert.Equal("chunk-1", result.Id);
        Assert.Equal("Example chunk.", result.Text);
        Assert.Equal(0.87, result.Score);
        Assert.Equal("Doc", result.SourceName);
        Assert.Equal("https://example.test", result.SourceUrl);
        Assert.Equal("docs", result.Metadata["category"].AsString);
        Assert.Equal("chunk-1", result.RawDocument["_id"].AsString);
        // The complete original document survives mapping, not just the configured field mappings.
        Assert.Equal("docs", result.RawDocument["category"].AsString);
        Assert.Equal("Doc", result.RawDocument["source"].AsBsonDocument["name"].AsString);
        // The internal reserved score alias must never leak into the public raw document.
        Assert.False(result.RawDocument.Contains("_ragScore"));
    }

    [Fact]
    public async Task PipelineDoesNotIncludeANarrowingProjectStage()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 0.5 } }],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.SearchAsync("query");

        Assert.Equal(2, state.AggregateStages.Count);
        Assert.DoesNotContain(state.AggregateStages, stage => stage.Contains("$project"));
    }

    [Fact]
    public async Task MissingRagScoreFieldIsAMappingError()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" } }],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBMappingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task NonNumericRagScoreIsAMappingError()
    {
        var state = new RAGCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "chunk-1" },
                    { "text", "chunk" },
                    { "_ragScore", "not-a-number" },
                },
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBMappingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task NonFiniteRagScoreIsAMappingError()
    {
        var state = new RAGCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "chunk-1" },
                    { "text", "chunk" },
                    { "_ragScore", double.NaN },
                },
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBMappingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task RawEmbeddingGeneratorFailuresAreWrappedAsEmbeddingExceptions()
    {
        var embeddings = new RecordingEmbeddingGenerator
        {
            FailWith = new InvalidOperationException("embedding service unavailable"),
        };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), embeddings);

        MongoDBEmbeddingException exception = await Assert.ThrowsAsync<MongoDBEmbeddingException>(
            () => provider.SearchAsync("query"));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task AnnStageOmitsExactAndUsesNumCandidates()
    {
        var state = new RAGCollectionState();
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            TopK = 5,
            NumCandidates = 150,
        };
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        await provider.SearchAsync("query");

        BsonDocument vectorSearch = state.AggregateStages[0]["$vectorSearch"].AsBsonDocument;
        Assert.Equal(150, vectorSearch["numCandidates"].AsInt32);
        Assert.Equal(5, vectorSearch["limit"].AsInt32);
        Assert.False(vectorSearch.Contains("exact"));
    }

    [Fact]
    public async Task EnnStageOmitsNumCandidatesAndSetsExactTrue()
    {
        var state = new RAGCollectionState();
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorEnn,
        };
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        await provider.SearchAsync("query");

        BsonDocument vectorSearch = state.AggregateStages[0]["$vectorSearch"].AsBsonDocument;
        Assert.True(vectorSearch["exact"].AsBoolean);
        Assert.False(vectorSearch.Contains("numCandidates"));
    }

    [Fact]
    public async Task EmptyQueryIsRejected()
    {
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState());

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() => provider.SearchAsync("   "));
    }

    [Fact]
    public async Task EmbeddingDimensionMismatchIsRejected()
    {
        var embeddings = new RecordingEmbeddingGenerator { Dimensions = 2 };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), embeddings);

        await Assert.ThrowsAsync<MongoDBEmbeddingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task NonFiniteEmbeddingValuesAreRejected()
    {
        var embeddings = new RecordingEmbeddingGenerator
        {
            EmbeddingFactory = _ => [float.NaN, 0.1f, 0.2f],
        };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), embeddings);

        await Assert.ThrowsAsync<MongoDBEmbeddingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task MissingRequiredIdFieldIsAMappingError()
    {
        var state = new RAGCollectionState
        {
            // A valid _ragScore is included so this test actually exercises the missing-ID mapping path rather
            // than failing earlier on score validation.
            Results = [new BsonDocument { { "text", "chunk" }, { "_ragScore", 0.5 } }],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBMappingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task MissingRequiredTextFieldIsAMappingError()
    {
        var state = new RAGCollectionState
        {
            // A valid _ragScore is included so this test actually exercises the missing-text mapping path rather
            // than failing earlier on score validation.
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "_ragScore", 0.5 } }],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBMappingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task MissingOptionalFieldsProduceNullRatherThanFailing()
    {
        var state = new RAGCollectionState
        {
            Results =
            [
                new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 0.5 } },
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        MongoDBRAGResult result = Assert.Single(await provider.SearchAsync("query"));

        Assert.Null(result.SourceName);
        Assert.Null(result.SourceUrl);
        Assert.Empty(result.Metadata);
    }

    [Fact]
    public async Task RetrievalFailuresAreTranslatedToAnActionableException()
    {
        var state = new RAGCollectionState
        {
            AggregateException = new MongoConnectionException(
                new ConnectionId(
                    new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                "offline"),
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBRetrievalException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task CancellationPropagatesRatherThanBeingTranslated()
    {
        var embeddings = new RecordingEmbeddingGenerator { Delay = TimeSpan.FromSeconds(5) };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), embeddings);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchAsync("query", cancellation.Token));
    }

    [Fact]
    public async Task RetrievalTimeoutIsTranslatedToATimeoutException()
    {
        var embeddings = new RecordingEmbeddingGenerator { Delay = TimeSpan.FromSeconds(5) };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            RetrievalTimeout = TimeSpan.FromMilliseconds(20),
        };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), embeddings, options);

        await Assert.ThrowsAsync<MongoDBTimeoutException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task SearchNeverIssuesAWriteOperation()
    {
        // The read-only test double throws NotSupportedException for any call other than AggregateAsync (and the
        // metadata accessors), so a passing search is itself proof that no write path was exercised.
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 0.5 } }],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.SearchAsync("query");
    }

    [Fact]
    public async Task FullTextSearchBuildsASearchStageWithTheConfiguredIndexAndTextFields()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 1.5 } }],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            SearchIndexName = "search_index",
            SearchTextFieldNames = ["title", "body"],
            TopK = 4,
        };
        MongoDBRAGProvider provider = CreateFullTextProvider(state, options);

        await provider.SearchAsync("blue widgets");

        BsonDocument search = state.AggregateStages[0]["$search"].AsBsonDocument;
        Assert.Equal("search_index", search["index"].AsString);
        BsonDocument textClause = search["compound"]["must"].AsBsonArray[0].AsBsonDocument["text"].AsBsonDocument;
        Assert.Equal("blue widgets", textClause["query"].AsString);
        Assert.Equal(new BsonArray(["title", "body"]), textClause["path"].AsBsonArray);
        Assert.Equal(new BsonDocument("$limit", 4), state.AggregateStages[1]);
    }

    [Fact]
    public async Task FullTextSearchPlacesTheMandatoryFilterInsideCompoundFilter()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 1.5 } }],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
        };
        MongoDBRAGProvider provider = CreateFullTextProvider(state, options);

        await provider.SearchAsync("blue widgets");

        BsonArray filter = state.AggregateStages[0]["$search"]["compound"]["filter"].AsBsonArray;
        Assert.Equal(
            BsonDocument.Parse("""{"equals":{"path":"tenant_id","value":"tenant-a"}}"""),
            filter[0].AsBsonDocument);
    }

    [Fact]
    public async Task FullTextSearchDoesNotRequireOrInvokeAnEmbeddingGeneratorEvenWhenOneIsConfigured()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 1.5 } }],
        };
        var embeddings = new RecordingEmbeddingGenerator();
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText };
        // Uses the vector-family constructor (which still accepts an embedding generator) with FullText mode, to
        // prove EmbedAsync is never invoked for this mode regardless of which constructor family was used.
        MongoDBRAGProvider provider = CreateProvider(state, embeddings, options);

        await provider.SearchAsync("blue widgets");

        Assert.Empty(embeddings.Calls);
    }

    [Fact]
    public async Task FullTextSearchUsesTheNativeSearchScoreAndPreservesTheRawDocument()
    {
        var state = new RAGCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "chunk-1" },
                    { "text", "Example chunk." },
                    { "_ragScore", 4.2 },
                    { "category", "docs" },
                },
            ],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            MetadataFieldNames = ["category"],
        };
        MongoDBRAGProvider provider = CreateFullTextProvider(state, options);

        MongoDBRAGResult result = Assert.Single(await provider.SearchAsync("blue widgets"));

        Assert.Equal(4.2, result.Score);
        Assert.Equal("docs", result.RawDocument["category"].AsString);
        Assert.False(result.RawDocument.Contains("_ragScore"));
    }

    [Fact]
    public async Task FullTextSearchDoesNotIncludeANarrowingProjectStage()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 1.0 } }],
        };
        MongoDBRAGProvider provider = CreateFullTextProvider(
            state,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText });

        await provider.SearchAsync("query");

        Assert.Equal(3, state.AggregateStages.Count);
        Assert.DoesNotContain(state.AggregateStages, stage => stage.Contains("$project"));
    }

    [Fact]
    public async Task HybridSearchLeadsWithRankFusionAndPlacesIndependentFiltersInBothInputs()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 0.5 } }],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
        };
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        await provider.SearchAsync("blue widgets");

        BsonDocument rankFusion = state.AggregateStages[0]["$rankFusion"].AsBsonDocument;
        BsonDocument pipelines = rankFusion["input"]["pipelines"].AsBsonDocument;
        BsonDocument vectorSearch = pipelines["vector"].AsBsonArray[0].AsBsonDocument["$vectorSearch"].AsBsonDocument;
        BsonDocument search = pipelines["text"].AsBsonArray[0].AsBsonDocument["$search"].AsBsonDocument;
        Assert.Equal(
            BsonDocument.Parse("""{"tenant_id":{"$eq":"tenant-a"}}"""),
            vectorSearch["filter"].AsBsonDocument);
        Assert.Equal(
            BsonDocument.Parse("""{"equals":{"path":"tenant_id","value":"tenant-a"}}"""),
            search["compound"]["filter"].AsBsonArray[0].AsBsonDocument);
    }

    [Fact]
    public async Task HybridSearchUsesConfiguredWeightsAndCandidateLimits()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 0.5 } }],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorWeight = 2.0,
            TextWeight = 0.5,
            VectorCandidateLimit = 25,
            TextCandidateLimit = 30,
            TopK = 7,
        };
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        await provider.SearchAsync("blue widgets");

        BsonDocument rankFusion = state.AggregateStages[0]["$rankFusion"].AsBsonDocument;
        BsonDocument weights = rankFusion["combination"]["weights"].AsBsonDocument;
        Assert.Equal(2.0, weights["vector"].ToDouble());
        Assert.Equal(0.5, weights["text"].ToDouble());
        BsonDocument pipelines = rankFusion["input"]["pipelines"].AsBsonDocument;
        Assert.Equal(25, pipelines["vector"].AsBsonArray[0]["$vectorSearch"]["limit"].AsInt32);
        Assert.Equal(30, pipelines["text"].AsBsonArray[1]["$limit"].AsInt32);
        Assert.Equal(new BsonDocument("$limit", 7), state.AggregateStages[1]);
    }

    [Fact]
    public async Task HybridSearchCapturesTheFusedScoreAndPreservesTheRawDocument()
    {
        var state = new RAGCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "chunk-1" },
                    { "text", "Example chunk." },
                    { "_ragScore", 0.031 },
                    { "category", "docs" },
                },
            ],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            MetadataFieldNames = ["category"],
        };
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        MongoDBRAGResult result = Assert.Single(await provider.SearchAsync("blue widgets"));

        Assert.Equal(0.031, result.Score);
        Assert.Equal("docs", result.RawDocument["category"].AsString);
        Assert.False(result.RawDocument.Contains("_ragScore"));
        Assert.Null(result.ScoreDetails);
    }

    [Fact]
    public async Task HybridSearchIncludesScoreDetailsOnlyWhenRequested()
    {
        var detailsDoc = new BsonDocument { { "value", 0.031 } };
        var state = new RAGCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "chunk-1" },
                    { "text", "chunk" },
                    { "_ragScore", 0.031 },
                    { "_ragScoreDetails", detailsDoc },
                },
            ],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            IncludeScoreDetails = true,
        };
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        MongoDBRAGResult result = Assert.Single(await provider.SearchAsync("blue widgets"));

        Assert.Equal(detailsDoc, result.ScoreDetails);
        Assert.False(result.RawDocument.Contains("_ragScoreDetails"));

        BsonDocument rankFusion = state.AggregateStages[0]["$rankFusion"].AsBsonDocument;
        Assert.True(rankFusion["scoreDetails"].AsBoolean);
    }

    [Fact]
    public async Task HybridSearchDoesNotIncludeANarrowingProjectStage()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 0.5 } }],
        };
        MongoDBRAGProvider provider = CreateProvider(
            state,
            options: new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.HybridRrf });

        await provider.SearchAsync("query");

        Assert.Equal(3, state.AggregateStages.Count);
        Assert.DoesNotContain(state.AggregateStages, stage => stage.Contains("$project"));
    }

    private static MongoDBRAGProvider CreateProvider(
        RAGCollectionState state,
        RecordingEmbeddingGenerator? embeddings = null,
        MongoDBRAGProviderOptions? options = null) =>
        new(
            RAGCollectionProxy.Create(state),
            embeddings ?? new RecordingEmbeddingGenerator(),
            3,
            options ?? new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn });

    private static MongoDBRAGProvider CreateFullTextProvider(
        RAGCollectionState state,
        MongoDBRAGProviderOptions? options = null) =>
        new(
            RAGCollectionProxy.Create(state),
            options ?? new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText });
}
