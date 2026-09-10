using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.Persistence;

/// <summary>
/// Adversarial constructor/lifecycle tests for <see cref="MongoDBAgentSessionStore"/>: proves the resolved
/// <c>Microsoft.Agents.AI.Abstractions</c> assembly version is validated and rejected outside the verified
/// range (docs/development/persistence/dotnet-contract-research.md), and that every constructor argument is
/// validated entirely before an owned client is created -- including that an owned client created just before a
/// later validation/connection step fails is still disposed even though no
/// <see cref="MongoDBAgentSessionStore"/> instance is ever returned to the caller (mirrors
/// <c>MongoDBMemoryIndexManagerLifecycleTests</c>/<c>MongoDBRAGIndexManagerLifecycleTests</c>).
/// </summary>
public sealed class MongoDBAgentSessionStoreLifecycleTests
{
    private static MongoDBAgentSessionStoreOptions ValidOptions => new()
    {
        ApplicationId = "app",
        AgentId = "agent",
    };

    [Fact]
    public void ConstructorAcceptsAResolvedVersionWithinTheSupportedRange()
    {
        var state = new SessionCollectionState();
        MongoDBAgentSessionStore store = new(
            SessionCollectionProxy.Create(state),
            ValidOptions,
            () => new Version(1, 16, 0, 0));

        Assert.False(store.OwnsClient);
    }

    [Fact]
    public void ConstructorRejectsAResolvedVersionBelowTheMinimumSupportedFloor()
    {
        var state = new SessionCollectionState();

        MongoDBConfigurationException exception = Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBAgentSessionStore(
                SessionCollectionProxy.Create(state),
                ValidOptions,
                () => new Version(1, 12, 0, 0)));

        Assert.Contains("Microsoft.Agents.AI.Abstractions", exception.Message);
    }

    [Fact]
    public void ConstructorRejectsAResolvedVersionAtOrAboveTheExclusiveMaximum()
    {
        var state = new SessionCollectionState();

        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBAgentSessionStore(
                SessionCollectionProxy.Create(state),
                ValidOptions,
                () => new Version(1, 17, 0, 0)));
    }

    [Fact]
    public void DefaultConstructorResolvesAVersionWithinTheSupportedRangeFromTheLoadedFrameworkAssembly()
    {
        // Regression alarm: if the referenced Microsoft.Agents.AI.Abstractions package is ever bumped beyond the
        // verified range without updating MaximumSupportedFrameworkAssemblyVersionExclusive, every public
        // constructor -- exercised here via the real (non-seam) constructor -- must fail closed rather than
        // silently accept an unverified framework version.
        var state = new SessionCollectionState();

        MongoDBAgentSessionStore store = new(SessionCollectionProxy.Create(state), ValidOptions);

        Assert.False(store.OwnsClient);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesOptionsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBAgentSessionStore(
            "mongodb://localhost:27017",
            "database",
            "sessions",
            new MongoDBAgentSessionStoreOptions { ApplicationId = "   ", AgentId = "agent" },
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return SessionFakeMongoClientProxy.Create(new SessionFakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesTheFrameworkVersionBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBAgentSessionStore(
            "mongodb://localhost:27017",
            "database",
            "sessions",
            ValidOptions,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return SessionFakeMongoClientProxy.Create(new SessionFakeMongoClientState());
            },
            resolvedFrameworkAssemblyVersionProvider: () => new Version(2, 0, 0, 0)));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesTheDatabaseNameBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBAgentSessionStore(
            "mongodb://localhost:27017",
            databaseName: "   ",
            "sessions",
            ValidOptions,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return SessionFakeMongoClientProxy.Create(new SessionFakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorDisposesTheOwnedClientWhenLaterValidationFails()
    {
        var clientState = new SessionFakeMongoClientState
        {
            GetDatabaseException = new InvalidOperationException("boom"),
        };

        Assert.Throws<InvalidOperationException>(() => new MongoDBAgentSessionStore(
            "mongodb://localhost:27017",
            "database",
            "sessions",
            ValidOptions,
            clientFactory: _ => SessionFakeMongoClientProxy.Create(clientState)));

        // The client was created by the factory before GetDatabase failed; since no MongoDBAgentSessionStore
        // instance is ever returned to the caller, the constructor itself must dispose it or it would leak.
        Assert.Equal(1, clientState.DisposeCount);
    }

    [Fact]
    public async Task ConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        MongoDBAgentSessionStore store = new(
            "mongodb://localhost:27017",
            "database",
            "sessions",
            ValidOptions);

        Assert.True(store.OwnsClient);
        await store.DisposeAsync();
        await store.DisposeAsync();
    }
}
