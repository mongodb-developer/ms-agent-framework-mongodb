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

    [Theory]
    [InlineData(MongoDBSearchMode.FullText)]
    [InlineData(MongoDBSearchMode.HybridRrf)]
    public async Task UnsupportedModesAreRejectedBeforeAnyEmbeddingOrNetworkCall(MongoDBSearchMode mode)
    {
        var state = new RAGCollectionState();
        var embeddings = new RecordingEmbeddingGenerator();
        var options = new MongoDBRAGProviderOptions { SearchMode = mode };
        MongoDBRAGProvider provider = CreateProvider(state, embeddings, options);

        await Assert.ThrowsAsync<MongoDBCapabilityException>(() => provider.SearchAsync("query"));

        Assert.Empty(embeddings.Calls);
        Assert.Empty(state.AggregateStages);
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
            Results = [new BsonDocument { { "text", "chunk" } }],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBMappingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task MissingRequiredTextFieldIsAMappingError()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" } }],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBMappingException>(() => provider.SearchAsync("query"));
    }

    [Fact]
    public async Task MissingOptionalFieldsProduceNullRatherThanFailing()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" } }],
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
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" } }],
        };
        MongoDBRAGProvider provider = CreateProvider(state);

        await provider.SearchAsync("query");
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
}
