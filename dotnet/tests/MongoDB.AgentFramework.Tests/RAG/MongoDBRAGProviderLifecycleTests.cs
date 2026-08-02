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
    public void ConnectionStringConstructorDisposesOwnedClientWhenLaterValidationFails()
    {
        var clientState = new FakeMongoClientState
        {
            GetDatabaseException = new InvalidOperationException("boom"),
        };

        Assert.Throws<InvalidOperationException>(() => new MongoDBRAGProvider(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn },
            logger: null,
            clientFactory: _ => FakeMongoClientProxy.Create(clientState)));

        // The client was created by the factory before GetDatabase failed; since no MongoDBRAGProvider instance
        // is ever returned to the caller, the constructor itself must dispose it or it would otherwise leak.
        Assert.Equal(1, clientState.DisposeCount);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesArgumentsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGProvider(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new RecordingEmbeddingGenerator(),
            vectorDimensions: 0,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn },
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        // Argument validation that does not require a client runs first, so a validation failure never creates
        // (and therefore never needs to dispose) a client at all.
        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesOptionsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        // TopK is a "no client required" options failure that MongoDBRAGProviderOptions.Copy() (called from the
        // chained collection constructor) would eventually catch via its own internal Validate() call -- but only
        // after Connect has already created and handed off an owned client. Options.Validate() must run in Connect
        // itself before the client is created, exactly like every other client-independent argument, or this
        // failure mode creates a client with nothing left to dispose it.
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGProvider(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn, TopK = -1 },
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
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
