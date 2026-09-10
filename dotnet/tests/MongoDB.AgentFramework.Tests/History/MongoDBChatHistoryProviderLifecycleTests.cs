namespace MongoDB.AgentFramework.Tests.History;

/// <summary>
/// Adversarial constructor/lifecycle tests for <see cref="MongoDBChatHistoryProvider"/>'s connection-string
/// constructor: proves every argument/option is validated entirely before an owned client is created, and that
/// an owned client created just before a later validation step fails (for example resolving the
/// database/collection) is still disposed even though no <see cref="MongoDBChatHistoryProvider"/> instance is ever
/// returned to the caller. Mirrors <c>MongoDBRAGProviderLifecycleTests</c>' equivalent coverage for the same
/// construction-exception-safety design.
/// </summary>
public sealed class MongoDBChatHistoryProviderLifecycleTests
{
    [Fact]
    public async Task InjectedResourcesRemainCallerOwned()
    {
        var provider = new MongoDBChatHistoryProvider(
            HistoryCollectionProxy.Create(new HistoryCollectionState()),
            ValidOptions());

        await provider.DisposeAsync();
        await provider.DisposeAsync();

        Assert.False(provider.OwnsClient);
    }

    [Fact]
    public async Task ConnectionStringConstructorOwnsAndDisposesClientIdempotently()
    {
        var provider = new MongoDBChatHistoryProvider(
            "mongodb://localhost:27017",
            "database",
            "messages",
            ValidOptions());

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

        Assert.Throws<InvalidOperationException>(() => new MongoDBChatHistoryProvider(
            "mongodb://localhost:27017",
            "database",
            "messages",
            ValidOptions(),
            clientFactory: _ => FakeMongoClientProxy.Create(clientState)));

        // The client was created by the factory before GetDatabase failed; since no MongoDBChatHistoryProvider
        // instance is ever returned to the caller, the constructor itself must dispose it or it would otherwise
        // leak.
        Assert.Equal(1, clientState.DisposeCount);
    }

    [Fact]
    public void ConnectionStringConstructorValidatesArgumentsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBChatHistoryProvider(
            "mongodb://localhost:27017",
            databaseName: "   ",
            "messages",
            ValidOptions(),
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

        // MaxMessages is a "no client required" options failure that MongoDBChatHistoryProviderOptions.Validate()
        // (called from the chained collection constructor) would eventually catch -- but only after Connect had
        // already created and handed off an owned client, if options were re-validated there instead of before
        // client creation. Validate() must run entirely before the client is created, exactly like every other
        // client-independent argument, or this failure mode creates a client with nothing left to dispose it.
        Assert.Throws<MongoDBConfigurationException>(() => new MongoDBChatHistoryProvider(
            "mongodb://localhost:27017",
            "database",
            "messages",
            ValidOptions() with { MaxMessages = 0 },
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void ConnectionStringConstructorRejectsNullOptionsBeforeCreatingAClient()
    {
        bool clientFactoryInvoked = false;

        Assert.Throws<ArgumentNullException>(() => new MongoDBChatHistoryProvider(
            "mongodb://localhost:27017",
            "database",
            "messages",
            options: null!,
            clientFactory: _ =>
            {
                clientFactoryInvoked = true;
                return FakeMongoClientProxy.Create(new FakeMongoClientState());
            }));

        Assert.False(clientFactoryInvoked);
    }

    [Fact]
    public void NullOptionsAreRejectedForTheInjectedCollectionConstructor()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBChatHistoryProvider(
            HistoryCollectionProxy.Create(new HistoryCollectionState()),
            options: null!));
    }

    [Fact]
    public void NullCollectionIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoDBChatHistoryProvider(
            collection: null!,
            ValidOptions()));
    }

    private static MongoDBChatHistoryProviderOptions ValidOptions() =>
        new()
        {
            ApplicationId = "app",
            AgentId = "agent",
            SessionId = "session",
        };
}
