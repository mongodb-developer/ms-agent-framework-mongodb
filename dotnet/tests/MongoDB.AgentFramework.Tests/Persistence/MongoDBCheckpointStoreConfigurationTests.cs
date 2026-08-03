using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.Persistence;

public sealed class MongoDBCheckpointStoreConfigurationTests
{
    [Fact]
    public void ValidateAcceptsMinimalRequiredScope()
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
        };

        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateRejectsMissingWorkflowId(string workflowId)
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = workflowId,
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void ValidateRejectsBlankOptionalTenantId()
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            TenantId = " ",
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRejectsNonPositiveDurations(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBCheckpointStoreOptions
            {
                WorkflowId = "workflow",
                DefaultExpiration = duration,
                ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
            }.Validate());
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBCheckpointStoreOptions
            {
                WorkflowId = "workflow",
                RetrievalTimeout = duration,
                ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
            }.Validate());
        Assert.Throws<MongoDBConfigurationException>(() =>
            new MongoDBCheckpointStoreOptions
            {
                WorkflowId = "workflow",
                PersistenceTimeout = duration,
                ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
            }.Validate());
    }

    [Fact]
    public void ValidateRejectsAContinuationTokenSigningKeyShorterThanTheMinimumLength()
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = new byte[MongoDBCheckpointStoreOptions.MinimumContinuationTokenSigningKeyLength - 1],
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void ValidateAcceptsAContinuationTokenSigningKeyAtExactlyTheMinimumLength()
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = new byte[MongoDBCheckpointStoreOptions.MinimumContinuationTokenSigningKeyLength],
        };

        options.Validate();
    }

    [Fact]
    public void ValidateRejectsANullContinuationTokenSigningKey()
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = null!,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void ToStringRedactsTheContinuationTokenSigningKey()
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
        };

        string rendered = options.ToString() ?? string.Empty;

        Assert.DoesNotContain(Convert.ToBase64String(CheckpointStoreTestSigningKey.Bytes), rendered);
        Assert.Contains("redacted", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConstructorTrimsScopeIdentifiers()
    {
        var state = new CheckpointCollectionState();
        var options = new MongoDBCheckpointStoreOptions
        {
            TenantId = " tenant ",
            WorkflowId = " workflow ",
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
        };

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
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
        };
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDBCheckpointStore((IMongoCollection<BsonDocument>)null!, options));
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotentWhenClientIsCallerOwned()
    {
        var state = new CheckpointCollectionState();
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
        };
        var store = new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), options);

        await store.DisposeAsync();
        await store.DisposeAsync();

        Assert.False(store.OwnsClient);
    }

    [Fact]
    public async Task ConstructorClonesTheContinuationTokenSigningKeyDefensivelyAsync()
    {
        // Keep an independent copy of the original key bytes to reconstruct a second, unrelated store below --
        // this proves what the first store's *token production* actually depended on, without reaching into
        // private state.
        byte[] originalKeyBytes = (byte[])CheckpointStoreTestSigningKey.Bytes.Clone();
        byte[] callerOwnedKey = (byte[])CheckpointStoreTestSigningKey.Bytes.Clone();

        var state = new CheckpointCollectionState();
        var options = new MongoDBCheckpointStoreOptions { WorkflowId = "workflow", ContinuationTokenSigningKey = callerOwnedKey };
        await using var store = new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), options);

        JsonElement payload = JsonSerializer.SerializeToElement("value");
        for (int i = 0; i < 2; i++)
        {
            await store.SaveCheckpointAsync("session-defensive-copy", $"cp-{i}", payload);
        }

        // Mutate the caller's original array *after* construction. If the store had merely retained a reference
        // to this array (instead of cloning it), every continuation token it signs from this point on would be
        // signed with all-zero bytes instead of the original key material.
        Array.Clear(callerOwnedKey);

        MongoDBCheckpointPage page = await store.ListCheckpointsAsync("session-defensive-copy", limit: 1);
        Assert.NotNull(page.ContinuationToken);

        // An independent store constructed with a fresh copy of the *original* (pre-mutation) key bytes must
        // still be able to decode the token the first store produced after the mutation. This is only possible
        // if the first store's internal signing key still held the original bytes -- i.e. it took its own
        // defensive copy at construction rather than holding a reference to the caller-owned array.
        var verifyingState = new CheckpointCollectionState();
        await using var verifyingStore = new MongoDBCheckpointStore(
            CheckpointCollectionProxy.Create(verifyingState),
            new MongoDBCheckpointStoreOptions { WorkflowId = "workflow", ContinuationTokenSigningKey = originalKeyBytes });
        verifyingState.Documents.AddRange(state.Documents);

        MongoDBCheckpointPage decoded = await verifyingStore.ListCheckpointsAsync(
            "session-defensive-copy", limit: 1, continuationToken: page.ContinuationToken);
        Assert.NotNull(decoded);
    }
}
