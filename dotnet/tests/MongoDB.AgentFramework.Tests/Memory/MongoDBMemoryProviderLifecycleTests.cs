namespace MongoDB.AgentFramework.Tests.Memory;

/// <summary>
/// Adversarial constructor/lifecycle tests for <see cref="MongoDBMemoryProvider"/>'s connection-string
/// constructor: proves every argument/option is validated entirely before an owned client is created, and that
/// an owned client created just before a later validation step fails (for example resolving the
/// database/collection) is still disposed even though no <see cref="MongoDBMemoryProvider"/> instance is ever
/// returned to the caller. Mirrors <c>MongoDBRAGProviderLifecycleTests</c>' and
/// <c>MongoDBMemoryIndexManagerLifecycleTests</c>' equivalent coverage for the same
/// construction-exception-safety design.
/// </summary>
public sealed class MongoDBMemoryProviderLifecycleTests
{
    [Fact]
    public async Task InjectedResourcesRemainCallerOwned()
    {
        var embeddings = new RecordingEmbeddingGenerator();
        MongoDBMemoryProvider provider = new(
            MemoryCollectionProxy.Create(new MemoryCollectionState()),
            embeddings,
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")));

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
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")));

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

        Assert.Throws<InvalidOperationException>(() => new MongoDBMemoryProvider(
            "mongodb://localhost:27017",
            "database",
            "memories",
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")),
            options: null,
            logger: null,
            clientFactory: _ => FakeMongoClientProxy.Create(clientState)));

        // The client was created by the factory before GetDatabase failed; since no MongoDBMemoryProvider
        // instance is ever returned to the caller, the constructor itself must dispose it or it would otherwise
        // leak.
        Assert.Equal(1, clientState.DisposeCount);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesArgumentsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBMemoryProvider(
            "mongodb://localhost:27017",
            databaseName: "   ",
            "memories",
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")),
            options: null,
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
    public void ConnectionStringConstructorValidatesVectorDimensionsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBMemoryProvider(
            "mongodb://localhost:27017",
            "database",
            "memories",
            new RecordingEmbeddingGenerator(),
            vectorDimensions: 0,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")),
            options: null,
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesOptionsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        // NumCandidates < MaxResults is a "no client required" options failure that
        // MongoDBMemoryProviderOptions.Copy() (called from the chained collection constructor) would eventually
        // catch via its own internal Validate() call -- but only after Connect had already created and handed off
        // an owned client, if options were re-validated there instead of before client creation. Validate() must
        // run before the client is created, exactly like every other client-independent argument, or this failure
        // mode creates a client with nothing left to dispose it.
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBMemoryProvider(
            "mongodb://localhost:27017",
            "database",
            "memories",
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")),
            new MongoDBMemoryProviderOptions { NumCandidates = 1, MaxResults = 3 },
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesIndexNameAgainstTheAllowlistBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        // A fully functional client/database/collection stands by (constructing MemoryCollectionProxy never
        // throws), proving this is not merely a "GetDatabase throws" scenario. IndexName is well-formed enough to
        // satisfy MongoDBMemoryProviderOptions.Validate()'s plain non-empty check, but "bad/name" fails the
        // independent, stricter MongoDB Vector Search index-name allowlist (Internal.IndexName) that constructing
        // MongoDBVectorSearchIndexDefinition enforces. PrepareOptions must derive that index definition -- and
        // therefore reject "bad/name" -- entirely before Connect ever calls the client factory, or the owned
        // client would be created and then immediately leaked when the core constructor later failed to build the
        // index definition.
        var databaseState = new FakeMongoDatabaseState
        {
            Collection = MemoryCollectionProxy.Create(new MemoryCollectionState()),
        };
        var clientState = new FakeMongoClientState
        {
            Database = FakeMongoDatabaseProxy.Create(databaseState),
        };

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBMemoryProvider(
            "mongodb://localhost:27017",
            "database",
            "memories",
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")),
            new MongoDBMemoryProviderOptions { IndexName = "bad/name" },
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(clientState);
            }));

        // The client factory is never invoked at all: the strongest possible assertion, and stronger than merely
        // proving an already-created client gets disposed.
        Assert.False(clientFactoryInvoked);
        Assert.Equal(0, clientState.DisposeCount);
    }

    [Fact]
    public async Task ConnectionStringConstructorSucceedsWithAFullyFunctionalClientWhenEverythingIsValid()
    {
        // Companion positive control for the allowlist test above: proves the fully functional fake
        // client/database/collection chain (and the refactored PreparedConfiguration plumbing) still succeeds
        // end-to-end for valid input, so the negative assertion above is meaningful.
        var databaseState = new FakeMongoDatabaseState
        {
            Collection = MemoryCollectionProxy.Create(new MemoryCollectionState()),
        };
        var clientState = new FakeMongoClientState
        {
            Database = FakeMongoDatabaseProxy.Create(databaseState),
        };
        bool clientFactoryInvoked = false;

        MongoDBMemoryProvider provider = new(
            "mongodb://localhost:27017",
            "database",
            "memories",
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")),
            options: null,
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(clientState);
            });

        Assert.True(clientFactoryInvoked);
        Assert.True(provider.OwnsClient);
        await provider.DisposeAsync();
        Assert.Equal(1, clientState.DisposeCount);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesEmbeddingGeneratorBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<ArgumentNullException>(() => new MongoDBMemoryProvider(
            "mongodb://localhost:27017",
            "database",
            "memories",
            embeddingGenerator: null!,
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")),
            options: null,
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesStateFactoryBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<ArgumentNullException>(() => new MongoDBMemoryProvider(
            "mongodb://localhost:27017",
            "database",
            "memories",
            new RecordingEmbeddingGenerator(),
            3,
            stateFactory: null!,
            options: null,
            logger: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void NonPositiveVectorDimensionsAreRejectedForTheInjectedCollectionConstructor()
    {
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBMemoryProvider(
            MemoryCollectionProxy.Create(new MemoryCollectionState()),
            new RecordingEmbeddingGenerator(),
            0,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user"))));
    }

    [Fact]
    public void NullEmbeddingGeneratorIsRejectedForTheInjectedCollectionConstructor()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBMemoryProvider(
            MemoryCollectionProxy.Create(new MemoryCollectionState()),
            embeddingGenerator: null!,
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user"))));
    }

    [Fact]
    public void NullStateFactoryIsRejectedForTheInjectedCollectionConstructor()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBMemoryProvider(
            MemoryCollectionProxy.Create(new MemoryCollectionState()),
            new RecordingEmbeddingGenerator(),
            3,
            stateFactory: null!));
    }

    [Fact]
    public void NullCollectionIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBMemoryProvider(
            collection: null!,
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user"))));
    }
}
