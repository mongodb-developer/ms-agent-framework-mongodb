using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.Persistence;

public sealed class MongoDBAgentSessionStoreConfigurationTests
{
    [Fact]
    public void ValidateAcceptsMinimalRequiredScope()
    {
        var options = new MongoDBAgentSessionStoreOptions
        {
            ApplicationId = "app",
            AgentId = "agent",
        };

        options.Validate();
    }

    [Theory]
    [InlineData("", "agent")]
    [InlineData(" ", "agent")]
    [InlineData(null, "agent")]
    public void ValidateRejectsMissingApplicationId(string? applicationId, string agentId)
    {
        var options = new MongoDBAgentSessionStoreOptions
        {
            ApplicationId = applicationId!,
            AgentId = agentId,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsMissingAgentId()
    {
        var options = new MongoDBAgentSessionStoreOptions { ApplicationId = "app", AgentId = " " };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsBlankOptionalTenantAndUserId()
    {
        var tenantOptions = new MongoDBAgentSessionStoreOptions
        {
            ApplicationId = "app",
            AgentId = "agent",
            TenantId = " ",
        };
        var userOptions = new MongoDBAgentSessionStoreOptions
        {
            ApplicationId = "app",
            AgentId = "agent",
            UserId = " ",
        };

        Assert.Throws<MongoDBConfigurationException>(tenantOptions.Validate);
        Assert.Throws<MongoDBConfigurationException>(userOptions.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRejectsNonPositiveDurations(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBAgentSessionStoreOptions
            {
                ApplicationId = "app",
                AgentId = "agent",
                DefaultExpiration = duration,
            }.Validate());
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBAgentSessionStoreOptions
            {
                ApplicationId = "app",
                AgentId = "agent",
                RetrievalTimeout = duration,
            }.Validate());
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBAgentSessionStoreOptions
            {
                ApplicationId = "app",
                AgentId = "agent",
                PersistenceTimeout = duration,
            }.Validate());
    }

    [Fact]
    public void ConstructorTrimsScopeIdentifiers()
    {
        var state = new SessionCollectionState();
        var options = new MongoDBAgentSessionStoreOptions
        {
            TenantId = " tenant ",
            ApplicationId = " app ",
            AgentId = " agent ",
            UserId = " user ",
        };

        var store = new MongoDBAgentSessionStore(SessionCollectionProxy.Create(state), options);

        Assert.False(store.OwnsClient);
    }

    [Fact]
    public void ConstructorRejectsNullOptions()
    {
        var state = new SessionCollectionState();
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDBAgentSessionStore(SessionCollectionProxy.Create(state), null!));
    }

    [Fact]
    public void ConstructorRejectsNullCollection()
    {
        var options = new MongoDBAgentSessionStoreOptions { ApplicationId = "app", AgentId = "agent" };
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDBAgentSessionStore((IMongoCollection<BsonDocument>)null!, options));
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotentWhenClientIsCallerOwned()
    {
        var state = new SessionCollectionState();
        var options = new MongoDBAgentSessionStoreOptions { ApplicationId = "app", AgentId = "agent" };
        var store = new MongoDBAgentSessionStore(SessionCollectionProxy.Create(state), options);

        await store.DisposeAsync();
        await store.DisposeAsync();

        Assert.False(store.OwnsClient);
    }
}
