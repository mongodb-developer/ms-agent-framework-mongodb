using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using System.Security.Cryptography;
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

    // ---------------------------------------------------------------------------------------------------
    // Blocker 2: transactional, monotonic sequence allocation under concurrency.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentSaveCheckpointAsyncCallsAllocateSequenceAndAreListedInCommitOrder()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        using var writerBReachedGate = new ManualResetEventSlim(false);
        using var releaseWriterA = new ManualResetEventSlim(false);
        state.BeforeTransactionLockAcquire = callIndex =>
        {
            if (callIndex == 2)
            {
                writerBReachedGate.Set();
            }
        };
        state.BeforeTransactionBody = attempt =>
        {
            if (attempt == 1)
            {
                // Writer A now holds the gate. Block here until writer B has genuinely reached (and is
                // blocked on) the same gate, so the ordering asserted below reflects real contention rather
                // than incidental scheduling.
                Assert.True(writerBReachedGate.Wait(TimeSpan.FromSeconds(10)));
                Assert.True(releaseWriterA.Wait(TimeSpan.FromSeconds(10)));
            }
        };

        Task<MongoDBCheckpointRecord> writerA = Task.Run(
            () => store.SaveCheckpointAsync("session-interleave", "writer-a", payload));

        // Writer A must already be inside the gate (attempt 1) before writer B starts, so writer B is
        // guaranteed the next call index (2) instead of racing writer A for the first one.
        Assert.True(SpinWait.SpinUntil(() => state.TransactionAttempt >= 1, TimeSpan.FromSeconds(10)));

        Task<MongoDBCheckpointRecord> writerB = Task.Run(
            () => store.SaveCheckpointAsync("session-interleave", "writer-b", payload));

        Assert.True(writerBReachedGate.Wait(TimeSpan.FromSeconds(10)));
        releaseWriterA.Set();

        MongoDBCheckpointRecord recordA = await writerA;
        MongoDBCheckpointRecord recordB = await writerB;

        Assert.Equal(1L, recordA.Sequence);
        Assert.Equal(2L, recordB.Sequence);

        MongoDBCheckpointPage page = await store.ListCheckpointsAsync("session-interleave", limit: 10);
        Assert.Equal(["writer-a", "writer-b"], page.Items.Select(item => item.CheckpointId));
    }

    [Fact]
    public async Task SaveCheckpointAsyncThrowsCapabilityExceptionWhenDeploymentDoesNotSupportTransactions()
    {
        var state = new CheckpointCollectionState
        {
            TransactionsUnsupportedException = CheckpointCollectionProxy.TransactionsUnsupportedException(),
        };
        var store = CreateStore(state);

        MongoDBCapabilityException exception = await Assert.ThrowsAsync<MongoDBCapabilityException>(() =>
            store.SaveCheckpointAsync("session-x", "cp-1", JsonSerializer.SerializeToElement("value")));

        Assert.IsType<MongoCommandException>(exception.InnerException);
        Assert.Empty(state.Documents);
    }

    // ---------------------------------------------------------------------------------------------------
    // Blocker 3: canonical length-prefixed binary framing (no delimiter collisions).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdentitiesThatWouldCollideUnderDelimiterJoinedHashingRemainDistinct()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        // Under a naive '|'-joined-then-hashed document ID, "sess|A" + "|B" and "sess|A|" + "B" would both
        // flatten to the literal string "sess|A|B" and collide onto the same document ID.
        await store.SaveCheckpointAsync("sess|A", "|B", payload);
        await store.SaveCheckpointAsync("sess|A|", "B", payload);

        Assert.Equal(2, state.Documents.Count(document => document["doc_type"] == "checkpoint"));
        Assert.NotNull(await store.LoadCheckpointAsync("sess|A", "|B"));
        Assert.NotNull(await store.LoadCheckpointAsync("sess|A|", "B"));

        string[] ids = state.Documents
            .Where(document => document["doc_type"] == "checkpoint")
            .Select(document => document["_id"].AsString)
            .Distinct()
            .ToArray();
        Assert.Equal(2, ids.Length);
    }

    [Fact]
    public async Task ParentLineageMatchingIsExactEvenWhenIdentifiersContainDelimiterCharacters()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        await store.SaveCheckpointAsync("session-lineage", "root|1", payload);
        await store.SaveCheckpointAsync("session-lineage", "root", payload);
        await store.SaveCheckpointAsync("session-lineage", "child", payload, parentCheckpointId: "root|1");

        IEnumerable<CheckpointInfo> children = await store.RetrieveIndexAsync(
            "session-lineage", withParent: new CheckpointInfo("session-lineage", "root|1"));

        Assert.Equal(["child"], children.Select(child => child.CheckpointId));
    }

    // ---------------------------------------------------------------------------------------------------
    // Blocker 4: TTL/regular index partial filters isolate checkpoint documents in a shared collection.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureIndexesAsyncScopesRegularIndexesToCheckpointsAndTtlIndexToCheckpointsWithDateExpiry()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);

        await store.EnsureIndexesAsync();

        CreateIndexModel<BsonDocument> identity =
            state.CreatedIndexes.Single(model => model.Options.Name == "checkpoint_identity_lookup");
        CreateIndexModel<BsonDocument> sequence =
            state.CreatedIndexes.Single(model => model.Options.Name == "checkpoint_sequence_lookup");
        CreateIndexModel<BsonDocument> ttl =
            state.CreatedIndexes.Single(model => model.Options.Name == "checkpoint_expiration_ttl");

        Assert.Equal(new BsonDocument("doc_type", "checkpoint"), RenderFilter(identity.Options.PartialFilterExpression!));
        Assert.Equal(new BsonDocument("doc_type", "checkpoint"), RenderFilter(sequence.Options.PartialFilterExpression!));
        Assert.Equal(
            new BsonDocument { { "doc_type", "checkpoint" }, { "expires_at", new BsonDocument("$type", "date") } },
            RenderFilter(ttl.Options.PartialFilterExpression!));
    }

    [Fact]
    public async Task ValidateIndexesAsyncRejectsATtlIndexMissingCheckpointDocTypeIsolation()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        await store.EnsureIndexesAsync();

        // Simulate a legacy/hand-created TTL index that only checks expires_at's type, without isolating
        // checkpoint documents from any other doc_type sharing this collection (e.g. sequence_counter) --
        // must be rejected rather than silently accepted, since it could TTL-reap unrelated documents.
        int index = state.CreatedIndexes.FindIndex(model => model.Options.Name == "checkpoint_expiration_ttl");
        state.CreatedIndexes[index] = new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("expires_at"),
            new CreateIndexOptions<BsonDocument>
            {
                Name = "checkpoint_expiration_ttl",
                ExpireAfter = TimeSpan.Zero,
                PartialFilterExpression = new BsonDocument("expires_at", new BsonDocument("$type", "date")),
            });

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(() => store.ValidateIndexesAsync());
    }

    [Fact]
    public async Task ValidateIndexesAsyncRejectsARegularIndexMissingCheckpointDocTypeIsolation()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        await store.EnsureIndexesAsync();

        int index = state.CreatedIndexes.FindIndex(model => model.Options.Name == "checkpoint_sequence_lookup");
        state.CreatedIndexes[index] = new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys
                .Ascending("tenant_id").Ascending("workflow_id").Ascending("session_id").Ascending("sequence"),
            new CreateIndexOptions<BsonDocument> { Name = "checkpoint_sequence_lookup" });

        await Assert.ThrowsAsync<MongoDBIndexMismatchException>(() => store.ValidateIndexesAsync());
    }

    // ---------------------------------------------------------------------------------------------------
    // Blocker 5: configurable, redacted HMAC signing key for continuation tokens.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ContinuationTokensDecodeAcrossStoreInstancesSharingTheSameSigningKeyAndScope()
    {
        var state = new CheckpointCollectionState();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var storeA = CreateStore(state, signingKey: key);
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        for (int i = 0; i < 3; i++)
        {
            await storeA.SaveCheckpointAsync("session-token", $"cp-{i}", payload);
        }

        MongoDBCheckpointPage firstPage = await storeA.ListCheckpointsAsync("session-token", limit: 2);
        Assert.NotNull(firstPage.ContinuationToken);

        // A second store instance with the same key and same scope must decode the first store's token --
        // proves validity is determined by the configured secret key, not per-instance state.
        var storeB = CreateStore(state, signingKey: key);
        MongoDBCheckpointPage secondPage = await storeB.ListCheckpointsAsync(
            "session-token", limit: 2, continuationToken: firstPage.ContinuationToken);

        Assert.Single(secondPage.Items);
        Assert.Equal("cp-2", secondPage.Items[0].CheckpointId);
    }

    [Fact]
    public async Task ContinuationTokenIsRejectedWhenDecodedByAStoreConfiguredWithADifferentSigningKey()
    {
        var state = new CheckpointCollectionState();
        var storeA = CreateStore(state, signingKey: RandomNumberGenerator.GetBytes(32));
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        for (int i = 0; i < 3; i++)
        {
            await storeA.SaveCheckpointAsync("session-token-2", $"cp-{i}", payload);
        }

        MongoDBCheckpointPage firstPage = await storeA.ListCheckpointsAsync("session-token-2", limit: 2);
        Assert.NotNull(firstPage.ContinuationToken);

        var storeB = CreateStore(state, signingKey: RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<MongoDBConfigurationException>(() =>
            storeB.ListCheckpointsAsync("session-token-2", limit: 2, continuationToken: firstPage.ContinuationToken));
    }

    // ---------------------------------------------------------------------------------------------------
    // Blocker 6: stable exception wrapping for every public write/delete/read path.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveCheckpointAsyncWrapsAGenericDriverFailureAsPersistenceException()
    {
        var state = new CheckpointCollectionState
        {
            InsertException = CheckpointCollectionProxy.GenericServerErrorException(),
        };
        var store = CreateStore(state);

        MongoDBPersistenceException exception = await Assert.ThrowsAsync<MongoDBPersistenceException>(() =>
            store.SaveCheckpointAsync("session-x", "cp-1", JsonSerializer.SerializeToElement("value")));

        Assert.IsType<MongoCommandException>(exception.InnerException);
    }

    [Fact]
    public async Task LoadCheckpointAsyncWrapsAGenericDriverFailureAsRetrievalException()
    {
        var state = new CheckpointCollectionState
        {
            FindException = CheckpointCollectionProxy.GenericServerErrorException(),
        };
        var store = CreateStore(state);

        MongoDBRetrievalException exception = await Assert.ThrowsAsync<MongoDBRetrievalException>(
            () => store.LoadCheckpointAsync("session-x", "cp-1"));

        Assert.IsType<MongoCommandException>(exception.InnerException);
    }

    [Fact]
    public async Task ListCheckpointsAsyncWrapsAGenericDriverFailureAsRetrievalException()
    {
        var state = new CheckpointCollectionState
        {
            FindException = CheckpointCollectionProxy.GenericServerErrorException(),
        };
        var store = CreateStore(state);

        await Assert.ThrowsAsync<MongoDBRetrievalException>(() => store.ListCheckpointsAsync("session-x", limit: 10));
    }

    [Fact]
    public async Task GetLatestCheckpointAsyncWrapsAGenericDriverFailureAsRetrievalException()
    {
        var state = new CheckpointCollectionState
        {
            FindException = CheckpointCollectionProxy.GenericServerErrorException(),
        };
        var store = CreateStore(state);

        await Assert.ThrowsAsync<MongoDBRetrievalException>(() => store.GetLatestCheckpointAsync("session-x"));
    }

    [Fact]
    public async Task DeleteCheckpointAsyncWrapsAGenericDriverFailureAsPersistenceException()
    {
        var state = new CheckpointCollectionState
        {
            DeleteException = CheckpointCollectionProxy.GenericServerErrorException(),
        };
        var store = CreateStore(state);

        await Assert.ThrowsAsync<MongoDBPersistenceException>(() => store.DeleteCheckpointAsync("session-x", "cp-1"));
    }

    // ---------------------------------------------------------------------------------------------------
    // Blocker 7: PersistenceTimeout/RetrievalTimeout are enforced even when no caller token is available.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateCheckpointAsyncAppliesPersistenceTimeoutEvenWithNoCallerCancellationToken()
    {
        var state = new CheckpointCollectionState
        {
            FindDelay = async token => await Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        var store = CreateStore(state, persistenceTimeout: TimeSpan.FromMilliseconds(20));

        // JsonCheckpointStore.CreateCheckpointAsync -- the actual framework hook -- accepts no
        // CancellationToken parameter at all; this proves PersistenceTimeout is still enforced purely from
        // configuration, not merely when a caller happens to pass a token through the richer facade.
        await Assert.ThrowsAsync<MongoDBTimeoutException>(() =>
            store.CreateCheckpointAsync("session-timeout", JsonSerializer.SerializeToElement("value")).AsTask());
    }

    [Fact]
    public async Task LoadCheckpointAsyncAppliesRetrievalTimeoutEvenWithNoCallerCancellationToken()
    {
        var state = new CheckpointCollectionState
        {
            FindDelay = async token => await Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
            RetrievalTimeout = TimeSpan.FromMilliseconds(20),
        };
        var store = new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), options);

        await Assert.ThrowsAsync<MongoDBTimeoutException>(() => store.LoadCheckpointAsync("session-x", "cp-1"));
    }

    // ---------------------------------------------------------------------------------------------------
    // RetrieveIndexAsync applies exactly one overall RetrievalTimeout deadline across its whole multi-page
    // operation (never resetting per page), and bounds the whole enumeration to a stable upper-sequence
    // snapshot captured once at the start so continuous concurrent writers cannot make it unbounded.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetrieveIndexAsyncAppliesOneOverallRetrievalTimeoutAcrossTheWholeMultiPageOperation()
    {
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        await store.SaveCheckpointAsync("session-index-timeout", "cp-1", payload);
        await store.SaveCheckpointAsync("session-index-timeout", "cp-2", payload);

        // Every fake FindAsync call (the upper-bound snapshot lookup, then each page) delays 40ms.
        // RetrieveIndexAsync issues at least two such calls for these two checkpoints (the upper-bound lookup,
        // then one page). Each *individual* 40ms delay would comfortably fit inside a fresh 60ms budget if the
        // deadline were (incorrectly) reset per call/page; only a single deadline shared across the whole
        // operation makes their combined ~80ms elapsed time exceed the 60ms RetrievalTimeout and throw.
        state.FindDelay = async token => await Task.Delay(TimeSpan.FromMilliseconds(40), token);
        var options = new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "workflow",
            ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
            RetrievalTimeout = TimeSpan.FromMilliseconds(60),
        };
        var timeoutStore = new MongoDBCheckpointStore(CheckpointCollectionProxy.Create(state), options);

        // JsonCheckpointStore.RetrieveIndexAsync -- the actual framework hook -- accepts no CancellationToken
        // at all, proving the single overall deadline is enforced purely from configuration.
        await Assert.ThrowsAsync<MongoDBTimeoutException>(() =>
            timeoutStore.RetrieveIndexAsync("session-index-timeout").AsTask());
    }

    [Fact]
    public async Task RetrieveIndexAsyncExcludesCheckpointsCommittedAfterItsUpperBoundSnapshot()
    {
        const string SessionId = "session-snapshot-bound";
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");
        await store.SaveCheckpointAsync(SessionId, "cp-before-1", payload);
        await store.SaveCheckpointAsync(SessionId, "cp-before-2", payload);

        // As soon as RetrieveIndexAsync's page fetch begins (its second FindAsync call, right after the
        // upper-bound snapshot lookup is the first), commit additional checkpoints -- simulating a writer that
        // keeps committing new checkpoints while this enumeration is already in progress. Because the snapshot
        // upper bound was already captured before this delay runs, these late checkpoints must never appear in
        // the result, regardless of how many pages the enumeration still has left to fetch.
        var callCount = 0;
        state.FindDelay = async _ =>
        {
            if (Interlocked.Increment(ref callCount) == 2)
            {
                await store.SaveCheckpointAsync(SessionId, "cp-after-1", payload);
                await store.SaveCheckpointAsync(SessionId, "cp-after-2", payload);
            }
        };

        CheckpointInfo[] index = (await store.RetrieveIndexAsync(SessionId)).ToArray();

        Assert.Equal(["cp-before-1", "cp-before-2"], index.Select(info => info.CheckpointId));
        Assert.DoesNotContain(index, info => info.CheckpointId.StartsWith("cp-after-", StringComparison.Ordinal));

        // The excluded checkpoints genuinely committed (they are not lost, merely outside this snapshot), and a
        // fresh call -- capturing a new snapshot -- observes them.
        CheckpointInfo[] laterIndex = (await store.RetrieveIndexAsync(SessionId)).ToArray();
        Assert.Equal(4, laterIndex.Length);
    }

    [Fact]
    public async Task RetrieveIndexAsyncExcludesConcurrentInsertsMadeBetweenTwoRealInternalPages()
    {
        const string SessionId = "session-snapshot-bound-multipage";
        var state = new CheckpointCollectionState();
        var store = CreateStore(state);
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        // RetrieveIndexAsync pages internally in batches of 1,000; seeding one more than that forces a genuine
        // second internal page fetch (not merely a second call for a single small page).
        const int InitialCount = 1_001;
        for (int i = 0; i < InitialCount; i++)
        {
            await store.SaveCheckpointAsync(SessionId, $"cp-{i:D5}", payload);
        }

        // Call 1 is the upper-bound snapshot lookup, call 2 is the first (1,000-item) page, call 3 is the
        // second (1-item) page. Committing new checkpoints exactly when call 3 begins proves they are excluded
        // from *this* enumeration's second page even though they exist in the collection by the time that
        // page's query actually executes.
        var callCount = 0;
        state.FindDelay = async _ =>
        {
            if (Interlocked.Increment(ref callCount) == 3)
            {
                await store.SaveCheckpointAsync(SessionId, "cp-late-1", payload);
                await store.SaveCheckpointAsync(SessionId, "cp-late-2", payload);
            }
        };

        CheckpointInfo[] index = (await store.RetrieveIndexAsync(SessionId)).ToArray();

        Assert.Equal(InitialCount, index.Length);
        Assert.DoesNotContain(index, info => info.CheckpointId.StartsWith("cp-late-", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------
    // Public exception messages exclude scoped identifiers (tenant/workflow/session/checkpoint/parent):
    // messages are operation/category text only, never caller- or store-supplied identity values.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetrieveCheckpointAsyncKeyNotFoundExceptionMessageExcludesScopedIdentifiers()
    {
        const string SentinelTenantId = "sentinel-tenant-77c02e1f";
        const string SentinelSessionId = "sentinel-session-98216f3a";
        const string SentinelCheckpointId = "sentinel-checkpoint-4b7e1c9d";
        var state = new CheckpointCollectionState();
        var store = CreateStore(state, tenantId: SentinelTenantId);

        KeyNotFoundException exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.RetrieveCheckpointAsync(
                SentinelSessionId, new CheckpointInfo(SentinelSessionId, SentinelCheckpointId)).AsTask());

        Assert.DoesNotContain(SentinelSessionId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelCheckpointId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelTenantId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveWithConflictingPayloadExceptionMessageExcludesScopedIdentifiers()
    {
        const string SentinelTenantId = "sentinel-tenant-6a1b3c9d";
        const string SentinelSessionId = "sentinel-session-3f9a2b6c";
        const string SentinelCheckpointId = "sentinel-checkpoint-71d0e5aa";
        const string SentinelParentId = "sentinel-parent-9c4d8e12";
        var state = new CheckpointCollectionState();
        var store = CreateStore(state, tenantId: SentinelTenantId);
        JsonElement payload = JsonSerializer.SerializeToElement("value");

        await store.SaveCheckpointAsync(
            SentinelSessionId, SentinelCheckpointId, payload, parentCheckpointId: SentinelParentId);

        MongoDBConcurrencyException exception = await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.SaveCheckpointAsync(
                SentinelSessionId,
                SentinelCheckpointId,
                JsonSerializer.SerializeToElement("different-value"),
                parentCheckpointId: SentinelParentId));

        Assert.DoesNotContain(SentinelSessionId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelCheckpointId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelParentId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelTenantId, exception.Message, StringComparison.Ordinal);
    }

    private static BsonDocument RenderFilter(FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry));

    private static MongoDBCheckpointStore CreateStore(
        CheckpointCollectionState state,
        string? tenantId = null,
        TimeSpan? defaultExpiration = null,
        Func<DateTimeOffset>? clock = null,
        byte[]? signingKey = null,
        TimeSpan? persistenceTimeout = null)
    {
        var options = new MongoDBCheckpointStoreOptions
        {
            TenantId = tenantId,
            WorkflowId = "workflow",
            DefaultExpiration = defaultExpiration,
            ContinuationTokenSigningKey = signingKey ?? CheckpointStoreTestSigningKey.Bytes,
            PersistenceTimeout = persistenceTimeout,
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
