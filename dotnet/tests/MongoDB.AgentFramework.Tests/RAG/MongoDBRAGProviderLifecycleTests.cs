namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class MongoDBRAGProviderLifecycleTests
{
    [Fact]
    public async Task InjectedResourcesRemainCallerOwned()
    {
        var embeddings = new RecordingEmbeddingGenerator();
        MongoDBRAGProvider provider = new(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            embeddings,
            3,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn });

        await provider.DisposeAsync();
        await provider.DisposeAsync();

        Assert.False(provider.OwnsClient);
        Assert.Empty(embeddings.Calls);
    }

    [Fact]
    public async Task ConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        MongoDBRAGProvider provider = new(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn });

        Assert.True(provider.OwnsClient);
        await provider.DisposeAsync();
        await provider.DisposeAsync();
    }

    [Fact]
    public void NonPositiveVectorDimensionsAreRejected()
    {
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGProvider(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            new RecordingEmbeddingGenerator(),
            0,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn }));
    }

    [Fact]
    public void InvalidOptionsAreRejectedAtConstructionTime()
    {
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGProvider(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions
            {
                SearchMode = MongoDBSearchMode.VectorAnn,
                TopK = -1,
            }));
    }

    [Fact]
    public void NullCollectionIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBRAGProvider(
            collection: null!,
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn }));
    }

    [Fact]
    public void NullEmbeddingGeneratorIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBRAGProvider(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            embeddingGenerator: null!,
            3,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn }));
    }

    [Fact]
    public void NullOptionsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBRAGProvider(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            new RecordingEmbeddingGenerator(),
            3,
            options: null!));
    }
}
