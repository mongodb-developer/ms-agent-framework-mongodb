namespace MongoDB.AgentFramework.Tests.Persistence;

/// <summary>
/// Adversarial constructor/lifecycle tests for <see cref="MongoDBCheckpointStore"/>: proves the resolved
/// <c>Microsoft.Agents.AI.Workflows</c> assembly version is validated and rejected outside the verified range
/// (docs/development/persistence/dotnet-checkpoint-contract-research.md), and that every constructor argument is
/// validated entirely before an owned client is created -- including that an owned client created just before a
/// later validation/connection step fails is still disposed even though no <see cref="MongoDBCheckpointStore"/>
/// instance is ever returned to the caller (mirrors <c>MongoDBAgentSessionStoreLifecycleTests</c>).
/// </summary>
public sealed class MongoDBCheckpointStoreLifecycleTests
{
    private static MongoDBCheckpointStoreOptions ValidOptions => new() { WorkflowId = "workflow" };

    [Fact]
    public void ConstructorAcceptsAResolvedVersionWithinTheSupportedRange()
    {
        var state = new CheckpointCollectionState();
        MongoDBCheckpointStore store = new(
            CheckpointCollectionProxy.Create(state),
            ValidOptions,
            () => new Version(1, 16, 0, 0));

        Assert.False(store.OwnsClient);
    }

    [Fact]
    public void ConstructorRejectsAResolvedVersionBelowTheMinimumSupportedFloor()
    {
        var state = new CheckpointCollectionState();

        MongoDBConfigurationException exception = Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBCheckpointStore(
                CheckpointCollectionProxy.Create(state),
                ValidOptions,
                () => new Version(1, 12, 0, 0)));

        Assert.Contains("Microsoft.Agents.AI.Workflows", exception.Message);
    }

    [Fact]
    public void ConstructorRejectsAResolvedVersionAtOrAboveTheExclusiveMaximum()
    {
        var state = new CheckpointCollectionState();

        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBCheckpointStore(
                CheckpointCollectionProxy.Create(state),
                ValidOptions,
                () => new Version(1, 17, 0, 0)));
    }

    [Fact]
    public void DefaultConstructorResolvesAVersionWithinTheSupportedRangeFromTheLoadedFrameworkAssembly()
    {
        // Regression alarm: if the referenced Microsoft.Agents.AI.Workflows package is ever bumped beyond the
        // verified range without updating MaximumSupportedFrameworkAssemblyVersionExclusive, every public
        // constructor -- exercised here via the real (non-seam) constructor -- must fail closed rather than
        // silently accept an unverified framework version.
        var state = new CheckpointCollectionState();

        MongoDBCheckpointStore store = new(CheckpointCollectionProxy.Create(state), ValidOptions);

        Assert.False(store.OwnsClient);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesOptionsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBCheckpointStore(
            "mongodb://localhost:27017",
            "database",
            "checkpoints",
            new MongoDBCheckpointStoreOptions { WorkflowId = "   " },
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return CheckpointFakeMongoClientProxy.Create(new CheckpointFakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesTheFrameworkVersionBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBCheckpointStore(
            "mongodb://localhost:27017",
            "database",
            "checkpoints",
            ValidOptions,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return CheckpointFakeMongoClientProxy.Create(new CheckpointFakeMongoClientState());
            },
            resolvedFrameworkAssemblyVersionProvider: () => new Version(2, 0, 0, 0)));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesTheDatabaseNameBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBCheckpointStore(
            "mongodb://localhost:27017",
            databaseName: "   ",
            "checkpoints",
            ValidOptions,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return CheckpointFakeMongoClientProxy.Create(new CheckpointFakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorDisposesTheOwnedClientWhenLaterValidationFails()
    {
        var clientState = new CheckpointFakeMongoClientState
        {
            GetDatabaseException = new InvalidOperationException("boom"),
        };

        Assert.Throws<InvalidOperationException>(() => new MongoDBCheckpointStore(
            "mongodb://localhost:27017",
            "database",
            "checkpoints",
            ValidOptions,
            clientFactory: _ => CheckpointFakeMongoClientProxy.Create(clientState)));

        // The client was created by the factory before GetDatabase failed; since no MongoDBCheckpointStore
        // instance is ever returned to the caller, the constructor itself must dispose it or it would leak.
        Assert.Equal(1, clientState.DisposeCount);
    }

    [Fact]
    public async Task ConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        MongoDBCheckpointStore store = new(
            "mongodb://localhost:27017",
            "database",
            "checkpoints",
            ValidOptions);

        Assert.True(store.OwnsClient);
        await store.DisposeAsync();
        await store.DisposeAsync();
    }
}
