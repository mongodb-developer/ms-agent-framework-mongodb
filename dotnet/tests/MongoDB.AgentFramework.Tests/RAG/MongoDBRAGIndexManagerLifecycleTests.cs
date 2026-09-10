namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Adversarial constructor/lifecycle tests for <see cref="MongoDBRAGIndexManager"/>'s connection-string
/// constructor: proves every argument/definition is validated entirely before an owned client is created, and
/// that an owned client created just before a later validation step fails (for example resolving the
/// database/collection) is still disposed even though no <see cref="MongoDBRAGIndexManager"/> instance is ever
/// returned to the caller (docs/spec/features/index-management.md's caller-owned-vs-manager-owned disposal
/// semantics).
/// </summary>
public sealed class MongoDBRAGIndexManagerLifecycleTests
{
    [Fact]
    public async Task InjectedResourcesRemainCallerOwned()
    {
        var state = new RAGCollectionState();
        MongoDBRAGIndexManager manager = new(
            RAGCollectionProxy.Create(state),
            vectorDefinition: new MongoDBVectorSearchIndexDefinition("facade_vector", "embedding", 3));

        Assert.False(manager.OwnsClient);
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        MongoDBRAGIndexManager manager = new(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            vectorDefinition: new MongoDBVectorSearchIndexDefinition("facade_vector", "embedding", 3));

        Assert.True(manager.OwnsClient);
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public void ConnectionStringConstructorValidatesArgumentsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        // An empty databaseName is a "no client required" validation failure that RequireText catches inside
        // ConnectClient before MongoClientFactory.FromConnectionString ever runs.
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGIndexManager(
            "mongodb://localhost:27017",
            databaseName: "   ",
            "chunks",
            new MongoDBVectorSearchIndexDefinition("facade_vector", "embedding", 3),
            searchDefinition: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesAtLeastOneDefinitionBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        // Neither definition configured is a "no client required" validation failure that Connect catches before
        // MongoClientFactory.FromConnectionString ever runs, mirroring the collection constructor's own eager
        // check (ConstructorRequiresAtLeastOneDefinition).
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBRAGIndexManager(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            vectorDefinition: null,
            searchDefinition: null,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorDisposesOwnedClientWhenLaterValidationFails()
    {
        var clientState = new FakeMongoClientState
        {
            GetDatabaseException = new InvalidOperationException("boom"),
        };

        Assert.Throws<InvalidOperationException>(() => new MongoDBRAGIndexManager(
            "mongodb://localhost:27017",
            "database",
            "chunks",
            new MongoDBVectorSearchIndexDefinition("facade_vector", "embedding", 3),
            searchDefinition: null,
            clientFactory: _ => FakeMongoClientProxy.Create(clientState)));

        // The client was created by the factory before GetDatabase failed; since no MongoDBRAGIndexManager
        // instance is ever returned to the caller, the constructor itself must dispose it or it would leak.
        Assert.Equal(1, clientState.DisposeCount);
    }
}
