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
    public void ConnectionStringConstructorNeverEnumeratesOptionsListsAfterCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        // A caller-controlled IReadOnlyList that only tolerates a single enumeration proves the constructor
        // validates/copies MongoDBRAGProviderOptions exactly once, entirely before the owned client is created.
        // Before this fix, Connect validated options directly (one enumeration) and the chained collection
        // constructor separately called options.Copy() (a second, later enumeration) after the client already
        // existed; if that second enumeration ever threw, the just-created client leaked, because no
        // MongoDBRAGProvider instance was ever returned to dispose it. With the fix, the single validated/copied
        // snapshot is produced before ConnectClient runs, so a list that cannot tolerate a second read fails here
        // -- before any client is created -- instead of after.
        Assert.Throws<InvalidOperationException>(() => new MongoDBRAGProvider(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions
            {
                SearchMode = MongoDBSearchMode.VectorAnn,
                MetadataFieldNames = new SingleUseFieldNames(["field"], toleratedEnumerations: 1),
            },
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public async Task ConnectionStringConstructorOnlyEnumeratesOptionsListsOnceOverall()
    {
        // A list tolerating exactly the two reads one full Validate()+Copy() pass performs (the foreach inside
        // Validate and the collection-expression spread inside Copy) must succeed end to end, proving construction
        // never performs a second such pass after that snapshot is taken.
        MongoDBRAGProvider provider = new(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new RecordingEmbeddingGenerator(),
            3,
            new MongoDBRAGProviderOptions
            {
                SearchMode = MongoDBSearchMode.VectorAnn,
                MetadataFieldNames = new SingleUseFieldNames(["field"], toleratedEnumerations: 2),
            });

        Assert.True(provider.OwnsClient);
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

    [Fact]
    public async Task FullTextOnlyCollectionConstructorDoesNotRequireAnEmbeddingGenerator()
    {
        MongoDBRAGProvider provider = new(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText });

        await provider.DisposeAsync();

        Assert.False(provider.OwnsClient);
    }

    [Theory]
    [InlineData(MongoDBSearchMode.VectorAnn)]
    [InlineData(MongoDBSearchMode.VectorEnn)]
    [InlineData(MongoDBSearchMode.HybridRrf)]
    public void FullTextOnlyConstructorsRejectModesThatRequireVectorConfiguration(MongoDBSearchMode mode)
    {
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGProvider(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            new MongoDBRAGProviderOptions { SearchMode = mode }));
    }

    [Fact]
    public void FullTextOnlyCollectionConstructorRejectsNullCollection()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBRAGProvider(
            collection: null!,
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText }));
    }

    [Fact]
    public void FullTextOnlyCollectionConstructorRejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBRAGProvider(
            RAGCollectionProxy.Create(new RAGCollectionState()),
            options: null!));
    }

    [Fact]
    public async Task FullTextOnlyConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        MongoDBRAGProvider provider = new(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText });

        Assert.True(provider.OwnsClient);
        await provider.DisposeAsync();
        await provider.DisposeAsync();
    }

    [Fact]
    public void FullTextOnlyConnectionStringConstructorDisposesOwnedClientWhenLaterValidationFails()
    {
        var clientState = new FakeMongoClientState
        {
            GetDatabaseException = new InvalidOperationException("boom"),
        };

        Assert.Throws<InvalidOperationException>(() => new MongoDBRAGProvider(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText },
            logger: null,
            clientFactory: _ => FakeMongoClientProxy.Create(clientState)));

        Assert.Equal(1, clientState.DisposeCount);
    }

    [Fact]
    public void FullTextOnlyConnectionStringConstructorValidatesArgumentsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGProvider(
            "mongodb://localhost:27017",
            databaseName: string.Empty,
            "chunks",
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText },
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void FullTextOnlyConnectionStringConstructorValidatesOptionsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        // A VectorAnn mode requires configuration this constructor never supplies (no embedding generator), so it
        // must fail before a client is created, exactly like every other client-independent argument.
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGProvider(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn },
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void FullTextOnlyConnectionStringConstructorNeverEnumeratesOptionsListsAfterCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        // Mirrors ConnectionStringConstructorNeverEnumeratesOptionsListsAfterCreatingAClient for the FullText-only
        // family, which shares the same ConnectClient/options-snapshot refactor.
        Assert.Throws<InvalidOperationException>(() => new MongoDBRAGProvider(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new MongoDBRAGProviderOptions
            {
                SearchMode = MongoDBSearchMode.FullText,
                MetadataFieldNames = new SingleUseFieldNames(["field"], toleratedEnumerations: 1),
            },
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public async Task FullTextOnlyConnectionStringConstructorOnlyEnumeratesOptionsListsOnceOverall()
    {
        MongoDBRAGProvider provider = new(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new MongoDBRAGProviderOptions
            {
                SearchMode = MongoDBSearchMode.FullText,
                MetadataFieldNames = new SingleUseFieldNames(["field"], toleratedEnumerations: 2),
            });

        Assert.True(provider.OwnsClient);
        await provider.DisposeAsync();
    }
}
