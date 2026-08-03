using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using MongoDB.Bson;
using System.Text.Json;

namespace MongoDB.AgentFramework.Tests.Persistence;

public sealed class MongoDBCheckpointStoreBehaviorTests
{
    [Fact]
    public async Task SaveThenLoadRoundTripsPayloadBytesExactlyIncludingUnusualNumericLiterals()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonDocument.Parse(
            """
            {"kind":"pending_approval","bigInt":9007199254740993,"trailingZero":1.50000,"nested":{"a":[1,2,null]}}
            """).RootElement;

        MongoDBCheckpointRecord created = await store.SaveCheckpointAsync("session-1", "checkpoint-1", payload);

        Assert.Equal(1L, created.Sequence);
        BsonDocument stored = state.Documents.Single(document => document["doc_type"] == "checkpoint");
        Assert.Equal(MongoDBCheckpointStore.SchemaVersion, stored["schema_version"].AsInt32);

        // The exact framework JSON payload bytes must round-trip, including numeric literals a lossy
        // BsonDocument re-parse would corrupt (a bigint beyond double precision, a decimal with a trailing
        // zero) -- this preserves opaque, framework-internal state such as pending-approval/resumption data.
        MongoDBCheckpointRecord? loaded = await store.LoadCheckpointAsync("session-1", "checkpoint-1");
        Assert.NotNull(loaded);
        Assert.Equal("9007199254740993", loaded!.Payload.GetProperty("bigInt").GetRawText());
        Assert.Equal("1.50000", loaded.Payload.GetProperty("trailingZero").GetRawText());
        Assert.Equal("pending_approval", loaded.Payload.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task SaveWithIdenticalRetryConvergesWithoutConflictOrNewSequence()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("same-payload");

        MongoDBCheckpointRecord first = await store.SaveCheckpointAsync("session-2", "checkpoint-1", payload);
        MongoDBCheckpointRecord retry = await store.SaveCheckpointAsync("session-2", "checkpoint-1", payload);

        Assert.Equal(first.Sequence, retry.Sequence);
        Assert.Single(state.Documents, document => document["doc_type"] == "checkpoint");
    }

    [Fact]
    public async Task SaveWithConflictingPayloadThrowsConcurrencyException()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);

        await store.SaveCheckpointAsync("session-3", "checkpoint-1", JsonSerializer.SerializeToElement("first"));

        await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.SaveCheckpointAsync("session-3", "checkpoint-1", JsonSerializer.SerializeToElement("second")));
    }

    [Fact]
    public async Task SaveWithConflictingParentThrowsConcurrencyException()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        await store.SaveCheckpointAsync("session-4", "root", payload);
        await store.SaveCheckpointAsync("session-4", "child", payload, parentCheckpointId: "root");

        await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.SaveCheckpointAsync("session-4", "child", payload, parentCheckpointId: "different-root"));
    }

    [Fact]
    public async Task TenantAndWorkflowScopesAreIsolatedForTheSameSessionAndCheckpointId()
    {
        var state = new CheckpointCollectionState();
        var tenantAStore = CreateStore(state, tenantId: "tenant-a");
        var tenantBStore = CreateStore(state, tenantId: "tenant-b");
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        await tenantAStore.SaveCheckpointAsync("shared-session", "shared-checkpoint", payload);

        MongoDBCheckpointRecord? crossTenant =
            await tenantBStore.LoadCheckpointAsync("shared-session", "shared-checkpoint");
        MongoDBCheckpointRecord? sameTenant =
            await tenantAStore.LoadCheckpointAsync("shared-session", "shared-checkpoint");

        Assert.Null(crossTenant);
        Assert.NotNull(sameTenant);
    }

    [Fact]
    public async Task SequenceIsMonotonicAcrossSavesRegardlessOfTimestampOrder()
    {
        var state = new CheckpointCollectionState();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var store = CreateStore(state, clock: clock.Read);
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        MongoDBCheckpointRecord first = await store.SaveCheckpointAsync("session-5", "cp-1", payload);

        // The clock moves backward for the second save; sequence allocation must still be strictly increasing
        // because it is driven by an atomic counter, never by the (now out-of-order) timestamp.
        clock.Now -= TimeSpan.FromDays(1);
        MongoDBCheckpointRecord second = await store.SaveCheckpointAsync("session-5", "cp-2", payload);

        Assert.True(second.Sequence > first.Sequence);
        Assert.True(second.CreatedAt < first.CreatedAt);
    }

    [Fact]
    public async Task GetLatestCheckpointAsyncReturnsHighestSequenceNotNewestTimestamp()
    {
        var state = new CheckpointCollectionState();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var store = CreateStore(state, clock: clock.Read);
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        await store.SaveCheckpointAsync("session-6", "cp-1", payload);
        clock.Now -= TimeSpan.FromDays(1);
        MongoDBCheckpointRecord second = await store.SaveCheckpointAsync("session-6", "cp-2", payload);

        MongoDBCheckpointRecord? latest = await store.GetLatestCheckpointAsync("session-6");

        Assert.NotNull(latest);
        Assert.Equal(second.CheckpointId, latest!.CheckpointId);
    }

    [Fact]
    public async Task ListCheckpointsAsyncReturnsAscendingSequenceOrderAcrossStablePages()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        for (int i = 0; i < 5; i++)
        {
            await store.SaveCheckpointAsync("session-7", $"cp-{i}", payload);
        }

        var seen = new List<string>();
        string? token = null;
        do
        {
            MongoDBCheckpointPage page = await store.ListCheckpointsAsync("session-7", limit: 2, token);
            seen.AddRange(page.Items.Select(item => item.CheckpointId));
            token = page.ContinuationToken;
        } while (token is not null);

        Assert.Equal(["cp-0", "cp-1", "cp-2", "cp-3", "cp-4"], seen);
    }

    [Fact]
    public async Task ContinuationTokenFromADifferentScopeIsRejected()
    {
        var state = new CheckpointCollectionState();
        var storeA = CreateStore(state, tenantId: "tenant-a");
        var storeB = CreateStore(state, tenantId: "tenant-b");
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        for (int i = 0; i < 3; i++)
        {
            await storeA.SaveCheckpointAsync("session-8", $"cp-{i}", payload);
        }

        MongoDBCheckpointPage firstPage = await storeA.ListCheckpointsAsync("session-8", limit: 1);
        Assert.NotNull(firstPage.ContinuationToken);

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() =>
            storeB.ListCheckpointsAsync("session-8", limit: 1, firstPage.ContinuationToken));
    }

    [Fact]
    public async Task TamperedContinuationTokenIsRejected()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        for (int i = 0; i < 3; i++)
        {
            await store.SaveCheckpointAsync("session-9", $"cp-{i}", payload);
        }

        MongoDBCheckpointPage firstPage = await store.ListCheckpointsAsync("session-9", limit: 1);
        string tampered = firstPage.ContinuationToken![..^1] + (firstPage.ContinuationToken[^1] == 'a' ? 'b' : 'a');

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() =>
            store.ListCheckpointsAsync("session-9", limit: 1, tampered));
    }

    [Fact]
    public async Task DeleteCheckpointAsyncRemovesTheDocumentAndIsIdempotent()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        await store.SaveCheckpointAsync("session-10", "cp-1", JsonSerializer.SerializeToElement("value"));

        Assert.True(await store.DeleteCheckpointAsync("session-10", "cp-1"));
        Assert.Null(await store.LoadCheckpointAsync("session-10", "cp-1"));
        Assert.False(await store.DeleteCheckpointAsync("session-10", "cp-1"));
    }

    [Fact]
    public async Task LoadCheckpointAsyncReturnsNullWhenAbsent()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);

        Assert.Null(await store.LoadCheckpointAsync("session-11", "missing"));
    }

    [Fact]
    public async Task SaveCheckpointAsyncWithIncompatibleSchemaVersionThrowsMappingException()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        await store.SaveCheckpointAsync("session-12", "cp-1", payload);
        BsonDocument stored = state.Documents.Single(document => document["doc_type"] == "checkpoint");
        stored["schema_version"] = 999;

        await Assert.ThrowsAsync<MongoDBMappingException>(() => store.LoadCheckpointAsync("session-12", "cp-1"));
        await Assert.ThrowsAsync<MongoDBMappingException>(() =>
            store.SaveCheckpointAsync("session-12", "cp-1", payload));
    }

    [Fact]
    public async Task BranchedLineageTracksMultipleChildrenOfTheSameParent()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        await store.SaveCheckpointAsync("session-13", "root", payload);
        await store.SaveCheckpointAsync("session-13", "branch-a", payload, parentCheckpointId: "root");
        await store.SaveCheckpointAsync("session-13", "branch-b", payload, parentCheckpointId: "root");

        IEnumerable<CheckpointInfo> children =
            await store.RetrieveIndexAsync("session-13", withParent: new CheckpointInfo("session-13", "root"));

        Assert.Equal(["branch-a", "branch-b"], children.Select(child => child.CheckpointId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EnsureIndexesAsyncCreatesTheRequiredRegularAndTtlIndexesAndValidateIndexesAsyncSucceeds()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state, defaultExpiration: TimeSpan.FromDays(1));

        IReadOnlyList<string> created = await store.EnsureIndexesAsync();

        Assert.Contains("checkpoint_identity_lookup", created);
        Assert.Contains("checkpoint_sequence_lookup", created);
        Assert.Contains("checkpoint_expiration_ttl", created);
        await store.ValidateIndexesAsync();
    }

    [Fact]
    public async Task SaveCheckpointAsyncAppliesDefaultExpirationWhenNoneIsExplicitlyProvided()
    {
        var state = new CheckpointCollectionState();
        // A whole-second timestamp avoids a spurious mismatch against the BSON DateTime round-trip, which
        // truncates to millisecond precision (DateTimeOffset.UtcNow carries sub-millisecond ticks).
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var store = CreateStore(state, defaultExpiration: TimeSpan.FromHours(2), clock: () => now);

        MongoDBCheckpointRecord created =
            await store.SaveCheckpointAsync("session-14", "cp-1", JsonSerializer.SerializeToElement("value"));

        Assert.Equal(now + TimeSpan.FromHours(2), created.ExpiresAt);
    }

    [Fact]
    public async Task SaveCheckpointAsyncLeavesExpiresAtNullWhenNoDefaultOrExplicitExpiryIsConfigured()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);

        MongoDBCheckpointRecord created =
            await store.SaveCheckpointAsync("session-15", "cp-1", JsonSerializer.SerializeToElement("value"));

        Assert.Null(created.ExpiresAt);
    }

    [Fact]
    public async Task RealJsonCheckpointStoreRoundTripThroughCheckpointManagerResumesAtLatestCommittedCheckpoint()
    {
        // Exercises MongoDBCheckpointStore purely through the public Microsoft.Agents.AI.Workflows framework
        // surface (CheckpointManager.CreateJson + the three JsonCheckpointStore abstract hooks), proving the
        // store satisfies the real framework contract end-to-end, not just this package's own facade.
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        // CheckpointManager.CreateJson accepts MongoDBCheckpointStore as an ICheckpointStore<JsonElement> --
        // real proof the store satisfies the framework's own manager factory. CheckpointManager.GetLatestCheckpointAsync
        // itself is intentionally NOT called here: it was added to the framework after this package's verified
        // floor (present at 1.16.0, absent at 1.13.0 -- see
        // docs/development/persistence/dotnet-checkpoint-contract-research.md), unlike the three JsonCheckpointStore
        // abstract hooks this store overrides, which are identical across the whole verified range. "Latest" is
        // instead computed the same way that convenience method itself is documented to: the last entry of
        // RetrieveIndexAsync's commit-ordered result -- available and correct at every verified version.
        CheckpointManager manager = CheckpointManager.CreateJson(store);
        Assert.NotNull(manager);
        const string SessionId = "framework-session";

        var committed = new List<CheckpointInfo>();
        CheckpointInfo? parent = null;
        for (int i = 0; i < 4; i++)
        {
            JsonElement value = JsonSerializer.SerializeToElement(new { step = i, pending_approval = i == 2 });
            CheckpointInfo info = await store.CreateCheckpointAsync(SessionId, value, parent);
            committed.Add(info);
            parent = info;
        }

        IEnumerable<CheckpointInfo> index = (await store.RetrieveIndexAsync(SessionId)).ToArray();
        Assert.Equal(committed, index);

        CheckpointInfo latest = index.Last();
        Assert.Equal(committed[^1], latest);

        // Resume: reload the exact payload of the latest checkpoint through the framework hook.
        JsonElement resumed = await store.RetrieveCheckpointAsync(SessionId, latest);
        Assert.Equal(3, resumed.GetProperty("step").GetInt32());
        Assert.False(resumed.GetProperty("pending_approval").GetBoolean());

        // Resume from the pending-approval checkpoint specifically (branch point), proving arbitrary historical
        // checkpoints -- not only the latest -- remain independently retrievable and immutable.
        CheckpointInfo pendingApprovalCheckpoint = committed[2];
        JsonElement pendingApprovalValue = await store.RetrieveCheckpointAsync(SessionId, pendingApprovalCheckpoint);
        Assert.True(pendingApprovalValue.GetProperty("pending_approval").GetBoolean());
    }

    [Fact]
    public async Task RetrieveCheckpointAsyncThrowsKeyNotFoundExceptionWhenAbsent()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.RetrieveCheckpointAsync("session-16", new CheckpointInfo("session-16", "missing")).AsTask());
    }

    private static MongoDBCheckpointStore CreateStore(
        CheckpointCollectionState state,
        string? tenantId = null,
        TimeSpan? defaultExpiration = null,
        Func<DateTimeOffset>? clock = null)
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            TenantId = tenantId,
            WorkflowId = "workflow",
            DefaultExpiration = defaultExpiration,
        };
        return clock is null
            ? new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), options)
            : new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), options, clock);
    }

    /// <summary>
    /// A settable fake clock used to prove sequence allocation is independent of timestamp ordering, without a
    /// real sleep: <see cref="Read"/> is passed as the store's injected "now" provider.
    /// </summary>
    private sealed class MutableClock(DateTimeOffset initial)
    {
        public DateTimeOffset Now { get; set; } = initial;

        public DateTimeOffset Read() => Now;
    }
}
