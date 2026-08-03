using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.Persistence;

public sealed class MongoDBCheckpointStoreConfigurationTests
{
    [Fact]
    public void ValidateAcceptsMinimalRequiredScope()
    {
        var options = new MongoDBCheckpointStoreOptions { WorkflowId = "workflow" };

        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateRejectsMissingWorkflowId(string workflowId)
    {
        var options = new MongoDBCheckpointStoreOptions { WorkflowId = workflowId };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsBlankOptionalTenantId()
    {
        var options = new MongoDBCheckpointStoreOptions { WorkflowId = "workflow", TenantId = " " };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRejectsNonPositiveDurations(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBCheckpointStoreOptions { WorkflowId = "workflow", DefaultExpiration = duration }.Validate());
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBCheckpointStoreOptions { WorkflowId = "workflow", RetrievalTimeout = duration }.Validate());
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBCheckpointStoreOptions { WorkflowId = "workflow", PersistenceTimeout = duration }.Validate());
    }

    [Fact]
    public void ConstructorTrimsScopeIdentifiers()
    {
        var state = new CheckpointCollectionState();
        var options = new MongoDBCheckpointStoreOptions { TenantId = " tenant ", WorkflowId = " workflow " };

        var store = new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), options);

        Assert.False(store.OwnsClient);
    }

    [Fact]
    public void ConstructorRejectsNullOptions()
    {
        var state = new CheckpointCollectionState();
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), null!));
    }

    [Fact]
    public void ConstructorRejectsNullCollection()
    {
        var options = new MongoDBCheckpointStoreOptions { WorkflowId = "workflow" };
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDBCheckpointStore((IMongoCollection<BsonDocument>)null!, options));
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotentWhenClientIsCallerOwned()
    {
        var state = new CheckpointCollectionState();
        var options = new MongoDBCheckpointStoreOptions { WorkflowId = "workflow" };
        var store = new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), options);

        await store.DisposeAsync();
        await store.DisposeAsync();

        Assert.False(store.OwnsClient);
    }
}
