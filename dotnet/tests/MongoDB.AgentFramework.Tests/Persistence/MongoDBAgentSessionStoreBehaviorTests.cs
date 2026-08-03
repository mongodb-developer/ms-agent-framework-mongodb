using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using System.Runtime.CompilerServices;
using System.Text.Json;

#pragma warning disable MAAI001

namespace MongoDB.AgentFramework.Tests.Persistence;

public sealed class MongoDBAgentSessionStoreBehaviorTests
{
    [Fact]
    public async Task SessionStateRoundTripsLosslesslyIncludingUnknownValues()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        var bag = new AgentSessionStateBag();
        bag.SetValue("counter", (object)42);
        bag.SetValue("flag", (object)true);
        bag.SetValue("nested", new Dictionary<string, object?> { ["a"] = 1, ["b"] = "two", ["c"] = null });
        bag.SetValue("array", new[] { 1, 2, 3 });
        bag.SetValue(
            "unknown_future_field",
            (object)JsonDocument.Parse("""{"kind":"future","payload":[1,"two",false,null]}""").RootElement);

        MongoDBAgentSessionRecord created = await store.CreateAsync(
            "session-1",
            new TestSession(bag),
            agent);

        Assert.Equal("1", created.Version);
        BsonDocument stored = state.Documents.Single();
        Assert.Equal(MongoDBAgentSessionStore.SchemaVersion, stored["schema_version"].AsInt32);
        Assert.Equal(1, stored["framework_version"].AsInt32);
        Assert.IsType<BsonDocument>(stored["session"]);

        MongoDBAgentSessionRecord? loaded = await store.GetAsync("session-1", agent);
        Assert.NotNull(loaded);
        AgentSessionStateBag restored = loaded!.Session.StateBag;
        Assert.Equal(42, ((JsonElement)restored.GetValue<object>("counter")!).GetInt32());
        Assert.True(((JsonElement)restored.GetValue<object>("flag")!).GetBoolean());
        Assert.Equal([1, 2, 3], restored.GetValue<int[]>("array")!);
        var unknown = (JsonElement)restored.GetValue<object>("unknown_future_field")!;
        Assert.Equal("future", unknown.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Array, unknown.GetProperty("payload").ValueKind);
        Assert.Equal(4, unknown.GetProperty("payload").GetArrayLength());
    }

    [Fact]
    public async Task TenantAndUserScopesAreIsolatedForTheSameSessionId()
    {
        var state = new SessionCollectionState();
        var tenantAStore = CreateStore(state, tenantId: "tenant-a");
        var tenantBStore = CreateStore(state, tenantId: "tenant-b");
        var agent = new FakeSessionAgent();

        await tenantAStore.CreateAsync("shared-id", new TestSession(), agent);

        MongoDBAgentSessionRecord? crossTenant = await tenantBStore.GetAsync("shared-id", agent);
        MongoDBAgentSessionRecord? sameTenant = await tenantAStore.GetAsync("shared-id", agent);

        Assert.Null(crossTenant);
        Assert.NotNull(sameTenant);
        Assert.Equal(1, state.Documents.Count(document => document["session_id"] == "shared-id"));
    }

    [Fact]
    public async Task CreateWithIdenticalRetryConvergesWithoutConflict()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        var bag = new AgentSessionStateBag();
        bag.SetValue("value", "same");
        var session = new TestSession(bag);

        MongoDBAgentSessionRecord first = await store.CreateAsync("session-2", session, agent);
        MongoDBAgentSessionRecord retry = await store.CreateAsync("session-2", new TestSession(bag), agent);

        Assert.Equal(first.Version, retry.Version);
        Assert.Single(state.Documents);
    }

    [Fact]
    public async Task CreateWithConflictingContentThrowsConcurrencyException()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        var firstBag = new AgentSessionStateBag();
        firstBag.SetValue("value", "first");
        var secondBag = new AgentSessionStateBag();
        secondBag.SetValue("value", "second");

        await store.CreateAsync("session-3", new TestSession(firstBag), agent);

        await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.CreateAsync("session-3", new TestSession(secondBag), agent));
    }

    [Fact]
    public async Task SetAsyncWithoutExpectedVersionUpsertsUnconditionally()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();

        MongoDBAgentSessionRecord created = await store.SetAsync("session-4", new TestSession(), agent);
        MongoDBAgentSessionRecord replaced = await store.SetAsync("session-4", new TestSession(), agent);

        Assert.Equal("1", created.Version);
        Assert.Equal("2", replaced.Version);
        Assert.Single(state.Documents);
    }

    [Fact]
    public async Task SetAsyncWithMatchingExpectedVersionAppliesCompareAndSwap()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        MongoDBAgentSessionRecord created = await store.CreateAsync("session-5", new TestSession(), agent);

        MongoDBAgentSessionRecord updated = await store.SetAsync(
            "session-5",
            new TestSession(),
            agent,
            expectedVersion: created.Version);

        Assert.Equal("2", updated.Version);
    }

    [Fact]
    public async Task SetAsyncWithStaleExpectedVersionThrowsConcurrencyException()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        var firstUpdateBag = new AgentSessionStateBag();
        firstUpdateBag.SetValue("value", "first-update");
        var conflictingBag = new AgentSessionStateBag();
        conflictingBag.SetValue("value", "conflicting-update");
        await store.CreateAsync("session-6", new TestSession(), agent);
        await store.SetAsync("session-6", new TestSession(firstUpdateBag), agent, expectedVersion: "1");

        await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.SetAsync("session-6", new TestSession(conflictingBag), agent, expectedVersion: "1"));
    }

    [Fact]
    public async Task SetAsyncRetryWithSameContentAfterCasSuccessConverges()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        var bag = new AgentSessionStateBag();
        bag.SetValue("value", "converge");
        MongoDBAgentSessionRecord created = await store.CreateAsync("session-7", new TestSession(bag), agent);

        MongoDBAgentSessionRecord updated = await store.SetAsync(
            "session-7",
            new TestSession(bag),
            agent,
            expectedVersion: created.Version);

        // Simulate a retried caller resending the exact same write with the pre-update expected version.
        MongoDBAgentSessionRecord retried = await store.SetAsync(
            "session-7",
            new TestSession(bag),
            agent,
            expectedVersion: created.Version);

        Assert.Equal(updated.Version, retried.Version);
    }

    [Fact]
    public async Task DeleteWithoutExpectedVersionIsIdempotentWhenAbsent()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);

        bool deleted = await store.DeleteAsync("missing-session");

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteRemovesMatchingSessionAndReturnsTrue()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        await store.CreateAsync("session-8", new TestSession(), agent);

        bool deleted = await store.DeleteAsync("session-8");

        Assert.True(deleted);
        Assert.Empty(state.Documents);
    }

    [Fact]
    public async Task DeleteWithStaleExpectedVersionThrowsConcurrencyException()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        await store.CreateAsync("session-9", new TestSession(), agent);

        await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.DeleteAsync("session-9", expectedVersion: "99"));
        Assert.Single(state.Documents);
    }

    [Fact]
    public async Task DefaultExpirationPopulatesExpiresAtWhenNotExplicitlyProvided()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state, defaultExpiration: TimeSpan.FromMinutes(30));
        var agent = new FakeSessionAgent();

        MongoDBAgentSessionRecord created = await store.CreateAsync("session-10", new TestSession(), agent);

        Assert.NotNull(created.ExpiresAt);
        Assert.True(created.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task NoExpirationConfiguredLeavesExpiresAtNull()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();

        MongoDBAgentSessionRecord created = await store.CreateAsync("session-11", new TestSession(), agent);

        Assert.Null(created.ExpiresAt);
    }

    [Fact]
    public async Task ExplicitExpiresAtOverridesDefaultExpiration()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state, defaultExpiration: TimeSpan.FromMinutes(30));
        var agent = new FakeSessionAgent();
        // BSON DateTime has millisecond precision; truncate to match the stored/round-tripped value.
        DateTimeOffset explicitExpiry = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds());

        MongoDBAgentSessionRecord created = await store.CreateAsync(
            "session-12",
            new TestSession(),
            agent,
            expiresAt: explicitExpiry);

        Assert.Equal(explicitExpiry, created.ExpiresAt);
    }

    [Fact]
    public async Task ListAsyncReturnsAscendingPagesWithContinuationToken()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        foreach (string id in new[] { "c", "a", "b" })
        {
            await store.CreateAsync(id, new TestSession(), agent);
        }

        MongoDBAgentSessionPage firstPage = await store.ListAsync(2);
        MongoDBAgentSessionPage secondPage = await store.ListAsync(2, firstPage.ContinuationToken);

        Assert.Equal(["a", "b"], firstPage.Items.Select(item => item.SessionId));
        Assert.NotNull(firstPage.ContinuationToken);
        Assert.Equal(["c"], secondPage.Items.Select(item => item.SessionId));
        Assert.Null(secondPage.ContinuationToken);
    }

    [Fact]
    public async Task ListAsyncRejectsOutOfRangeLimit()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() => store.ListAsync(0));
        await Assert.ThrowsAsync<MongoDBConfigurationException>(() => store.ListAsync(10_001));
    }

    [Fact]
    public async Task UnsupportedSchemaVersionIsRejectedWithActionableException()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        await store.CreateAsync("session-13", new TestSession(), agent);
        state.Documents[0]["schema_version"] = 999;

        await Assert.ThrowsAsync<MongoDBMappingException>(() => store.GetAsync("session-13", agent));
    }

    [Fact]
    public async Task UnsupportedFrameworkVersionIsRejectedWithActionableException()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        await store.CreateAsync("session-14", new TestSession(), agent);
        state.Documents[0]["framework_version"] = 999;

        await Assert.ThrowsAsync<MongoDBMappingException>(() => store.GetAsync("session-14", agent));
    }

    [Fact]
    public async Task GetAsyncPropagatesCancellation()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.GetAsync("session-15", agent, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task InvalidExpectedVersionTokenIsRejected()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() =>
            store.SetAsync("session-16", new TestSession(), agent, expectedVersion: "not-a-number"));
    }

    [Fact]
    public async Task EnsureAndValidateIndexesRoundTripSucceeds()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);

        await store.EnsureIndexesAsync();
        await store.ValidateIndexesAsync();
    }

    [Fact]
    public async Task ValidateIndexesFailsWhenIndexesAreMissing()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);

        await Assert.ThrowsAsync<MongoDBIndexMissingException>(() => store.ValidateIndexesAsync());
    }

    private static MongoDBAgentSessionStore CreateStore(
        SessionCollectionState state,
        string? tenantId = null,
        TimeSpan? defaultExpiration = null) =>
        new(
            SessionCollectionProxy.Create(state),
            new MongoDBAgentSessionStoreOptions
            {
                TenantId = tenantId,
                ApplicationId = "app",
                AgentId = "agent",
                DefaultExpiration = defaultExpiration,
            });

    private sealed class TestSession : AgentSession
    {
        public TestSession()
        {
        }

        public TestSession(AgentSessionStateBag stateBag)
            : base(stateBag)
        {
        }
    }

    private sealed class FakeSessionAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(session.StateBag.Serialize());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedSession,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestSession(AgentSessionStateBag.Deserialize(serializedSession)));

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
