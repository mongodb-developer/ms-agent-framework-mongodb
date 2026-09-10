using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
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
            (object)JsonDocument.Parse(
                """
                {"kind":"future","payload":[1,"two",false,null],"bigInt":9007199254740993,"trailingZero":1.50000}
                """).RootElement);

        MongoDBAgentSessionRecord created = await store.CreateAsync(
            "session-1",
            new TestSession(bag),
            agent);

        Assert.Equal("1", created.Version);
        BsonDocument stored = state.Documents.Single();
        Assert.Equal(MongoDBAgentSessionStore.SchemaVersion, stored["schema_version"].AsInt32);
        Assert.Equal(1, stored["framework_version"].AsInt32);

        // The public serializer's UTF-8 JSON bytes must be stored verbatim as BSON Binary, not re-parsed through
        // BsonDocument (which would lossily retype/reformat unusual numeric literals). Prove byte-for-byte
        // preservation of a bigint beyond double precision and a decimal with a trailing zero.
        BsonBinaryData storedPayload = Assert.IsType<BsonBinaryData>(stored["session"]);
        string storedJson = Encoding.UTF8.GetString(storedPayload.Bytes);
        Assert.Contains("9007199254740993", storedJson, StringComparison.Ordinal);
        Assert.Contains("1.50000", storedJson, StringComparison.Ordinal);

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
        Assert.Equal("9007199254740993", unknown.GetProperty("bigInt").GetRawText());
        Assert.Equal("1.50000", unknown.GetProperty("trailingZero").GetRawText());
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

    [Fact]
    public async Task CreateAsyncWithIncompatibleExistingSchemaThrowsMigrationExceptionWithoutMutating()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        await store.CreateAsync("session-17", new TestSession(), agent);
        state.Documents[0]["schema_version"] = 999;
        BsonDocument snapshot = state.Documents[0].DeepClone().AsBsonDocument;

        await Assert.ThrowsAsync<MongoDBMappingException>(() =>
            store.CreateAsync("session-17", new TestSession(), agent));

        Assert.Single(state.Documents);
        Assert.Equal(snapshot, state.Documents[0]);
    }

    [Fact]
    public async Task SetAsyncUpsertWithIncompatibleExistingSchemaThrowsMigrationExceptionWithoutMutating()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        await store.CreateAsync("session-18", new TestSession(), agent);
        state.Documents[0]["schema_version"] = 999;
        BsonDocument snapshot = state.Documents[0].DeepClone().AsBsonDocument;

        await Assert.ThrowsAsync<MongoDBMappingException>(() =>
            store.SetAsync("session-18", new TestSession(), agent));

        Assert.Single(state.Documents);
        Assert.Equal(snapshot, state.Documents[0]);
    }

    [Fact]
    public async Task SetAsyncWithExpectedVersionAndIncompatibleExistingSchemaThrowsMigrationExceptionWithoutMutating()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        MongoDBAgentSessionRecord created = await store.CreateAsync("session-19", new TestSession(), agent);
        state.Documents[0]["framework_version"] = 999;
        BsonDocument snapshot = state.Documents[0].DeepClone().AsBsonDocument;

        await Assert.ThrowsAsync<MongoDBMappingException>(() =>
            store.SetAsync("session-19", new TestSession(), agent, expectedVersion: created.Version));

        Assert.Single(state.Documents);
        Assert.Equal(snapshot, state.Documents[0]);
    }

    [Fact]
    public async Task DeleteAsyncWithIncompatibleExistingSchemaThrowsMigrationExceptionWithoutMutating()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        await store.CreateAsync("session-20", new TestSession(), agent);
        state.Documents[0]["schema_version"] = 999;
        BsonDocument snapshot = state.Documents[0].DeepClone().AsBsonDocument;

        await Assert.ThrowsAsync<MongoDBMappingException>(() => store.DeleteAsync("session-20"));
        Assert.Single(state.Documents);
        Assert.Equal(snapshot, state.Documents[0]);

        // The migration check must fire before any CAS-version comparison too -- an incompatible document is
        // never safely deletable regardless of whether the caller supplied an expectedVersion.
        await Assert.ThrowsAsync<MongoDBMappingException>(() =>
            store.DeleteAsync("session-20", expectedVersion: "1"));
        Assert.Single(state.Documents);
        Assert.Equal(snapshot, state.Documents[0]);
    }

    [Fact]
    public async Task CreateWithIdenticalContentButDifferentExpiryConflicts()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        var bag = new AgentSessionStateBag();
        bag.SetValue("value", "same");

        await store.CreateAsync(
            "session-21", new TestSession(bag), agent, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        // Identical content alone must not converge a retry: a different intended expiry is a genuine conflict,
        // not a duplicate retry of the same logical write.
        await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.CreateAsync(
                "session-21", new TestSession(bag), agent, expiresAt: DateTimeOffset.UtcNow.AddHours(2)));
    }

    [Fact]
    public async Task SetAsyncRetryWithSameContentButDifferentExpiryConflicts()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        var bag = new AgentSessionStateBag();
        bag.SetValue("value", "converge");
        MongoDBAgentSessionRecord created = await store.CreateAsync("session-22", new TestSession(bag), agent);

        await store.SetAsync(
            "session-22",
            new TestSession(bag),
            agent,
            expectedVersion: created.Version,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.SetAsync(
                "session-22",
                new TestSession(bag),
                agent,
                expectedVersion: created.Version,
                expiresAt: DateTimeOffset.UtcNow.AddHours(2)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(null)]
    public async Task WhitespaceOnlyOrNullSessionIdIsRejected(string? sessionId)
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();

        await Assert.ThrowsAsync<MongoDBConfigurationException>(() =>
            store.CreateAsync(sessionId!, new TestSession(), agent));
    }

    [Fact]
    public async Task LeadingAndTrailingWhitespaceSessionIdsAreDistinctAndReachable()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();

        await store.CreateAsync(" session-23", new TestSession(), agent);
        await store.CreateAsync("session-23 ", new TestSession(), agent);
        await store.CreateAsync("session-23", new TestSession(), agent);

        Assert.Equal(3, state.Documents.Count);
        MongoDBAgentSessionRecord? leading = await store.GetAsync(" session-23", agent);
        MongoDBAgentSessionRecord? trailing = await store.GetAsync("session-23 ", agent);
        MongoDBAgentSessionRecord? plain = await store.GetAsync("session-23", agent);

        Assert.NotNull(leading);
        Assert.NotNull(trailing);
        Assert.NotNull(plain);
        Assert.Equal(" session-23", leading!.SessionId);
        Assert.Equal("session-23 ", trailing!.SessionId);
        Assert.Equal("session-23", plain!.SessionId);
    }

    [Fact]
    public async Task ListAsyncExcludesExpiredSessions()
    {
        var state = new SessionCollectionState();
        var store = CreateStore(state);
        var agent = new FakeSessionAgent();
        await store.CreateAsync("session-24", new TestSession(), agent);
        await store.CreateAsync(
            "session-25", new TestSession(), agent, expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        await store.CreateAsync(
            "session-26", new TestSession(), agent, expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        MongoDBAgentSessionPage page = await store.ListAsync(10);

        Assert.Equal(["session-24", "session-25"], page.Items.Select(item => item.SessionId));
    }

    [Fact]
    public async Task CreateWithDefaultExpirationRetryConvergesAcrossElapsedTimeWithoutExtendingExpiry()
    {
        var state = new SessionCollectionState();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var store = CreateStore(state, defaultExpiration: TimeSpan.FromMinutes(30), clock: clock.Read);
        var agent = new FakeSessionAgent();
        var bag = new AgentSessionStateBag();
        bag.SetValue("value", "same");

        MongoDBAgentSessionRecord first = await store.CreateAsync("session-27", new TestSession(bag), agent);

        // Advance the fake clock so a retry's freshly recomputed default expiry (now + DefaultExpiration) would
        // differ from the one the first, successful attempt already persisted.
        clock.Now += TimeSpan.FromMinutes(10);
        MongoDBAgentSessionRecord retry = await store.CreateAsync("session-27", new TestSession(bag), agent);

        Assert.Equal(first.Version, retry.Version);
        Assert.Equal(first.ExpiresAt, retry.ExpiresAt);
        Assert.Single(state.Documents);
        Assert.Equal(first.ExpiresAt!.Value.UtcDateTime, state.Documents[0]["expires_at"].ToUniversalTime());
    }

    [Fact]
    public async Task CreateWithDefaultExpirationRetryAfterExistingExpiryHasPassedConflicts()
    {
        var state = new SessionCollectionState();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var store = CreateStore(state, defaultExpiration: TimeSpan.FromMinutes(30), clock: clock.Read);
        var agent = new FakeSessionAgent();
        var bag = new AgentSessionStateBag();
        bag.SetValue("value", "same");

        await store.CreateAsync("session-28", new TestSession(bag), agent);

        // Advance the fake clock past the persisted default expiry: the existing document is logically expired,
        // so it is not a compatible default-expiration convergence target even though the payload matches.
        clock.Now += TimeSpan.FromHours(1);
        await Assert.ThrowsAsync<MongoDBConcurrencyException>(() =>
            store.CreateAsync("session-28", new TestSession(bag), agent));
    }

    [Fact]
    public async Task SetAsyncCasRetryWithDefaultExpirationConvergesAcrossElapsedTimeWithoutExtendingExpiry()
    {
        var state = new SessionCollectionState();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var store = CreateStore(state, defaultExpiration: TimeSpan.FromMinutes(30), clock: clock.Read);
        var agent = new FakeSessionAgent();
        var bag = new AgentSessionStateBag();
        bag.SetValue("value", "converge");
        MongoDBAgentSessionRecord created = await store.CreateAsync("session-29", new TestSession(bag), agent);

        MongoDBAgentSessionRecord updated = await store.SetAsync(
            "session-29",
            new TestSession(bag),
            agent,
            expectedVersion: created.Version);

        // Simulate a retried caller resending the exact same write with the pre-update expected version, after
        // time has passed -- a freshly recomputed default expiry would differ from what was already persisted.
        clock.Now += TimeSpan.FromMinutes(10);
        MongoDBAgentSessionRecord retried = await store.SetAsync(
            "session-29",
            new TestSession(bag),
            agent,
            expectedVersion: created.Version);

        Assert.Equal(updated.Version, retried.Version);
        Assert.Equal(updated.ExpiresAt, retried.ExpiresAt);
        Assert.Equal(updated.ExpiresAt!.Value.UtcDateTime, state.Documents[0]["expires_at"].ToUniversalTime());
    }

    [Fact]
    public async Task SetAsyncIntentionalUpdateWithChangedPayloadGetsNewlyComputedDefaultExpiry()
    {
        var state = new SessionCollectionState();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var store = CreateStore(state, defaultExpiration: TimeSpan.FromMinutes(30), clock: clock.Read);
        var agent = new FakeSessionAgent();
        var originalBag = new AgentSessionStateBag();
        originalBag.SetValue("value", "original");
        MongoDBAgentSessionRecord created = await store.CreateAsync(
            "session-30", new TestSession(originalBag), agent);

        clock.Now += TimeSpan.FromMinutes(10);
        var changedBag = new AgentSessionStateBag();
        changedBag.SetValue("value", "changed");
        MongoDBAgentSessionRecord updated = await store.SetAsync(
            "session-30",
            new TestSession(changedBag),
            agent,
            expectedVersion: created.Version);

        // An actual content change is a genuine update, not a retry: it must get a freshly computed default
        // expiry based on the later "now", not the original create's expiry.
        Assert.NotEqual(created.ExpiresAt, updated.ExpiresAt);
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(30), updated.ExpiresAt);
    }

    private static MongoDBAgentSessionStore CreateStore(
        SessionCollectionState state,
        string? tenantId = null,
        TimeSpan? defaultExpiration = null,
        Func<DateTimeOffset>? clock = null)
    {
        var options = new MongoDBAgentSessionStoreOptions
        {
            TenantId = tenantId,
            ApplicationId = "app",
            AgentId = "agent",
            DefaultExpiration = defaultExpiration,
        };
        return clock is null
            ? new MongoDBAgentSessionStore(SessionCollectionProxy.Create(state), options)
            : new MongoDBAgentSessionStore(SessionCollectionProxy.Create(state), options, clock);
    }

    /// <summary>
    /// A settable fake clock used to prove default-expiration retry-convergence behavior across elapsed time
    /// without a real sleep: <see cref="Read"/> is passed as the store's injected "now" provider.
    /// </summary>
    private static MongoConnectionException ConnectionFailure() =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "simulated connection failure");

    // ---------------------------------------------------------------------------------------------------
    // A raw driver MongoException must never leak from a write path: every non-cancellation MongoException
    // (other than the specific duplicate-key/concurrency races each write path already interprets) is wrapped
    // as a stable MongoDBPersistenceException that preserves the original as InnerException, matching every
    // read path's existing MongoDBRetrievalException wrapping.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_WhenInsertFailsWithNonDuplicateKeyMongoException_WrapsAsPersistenceExceptionPreservingInner()
    {
        var state = new SessionCollectionState { InsertException = ConnectionFailure() };
        var store = CreateStore(state);

        MongoDBPersistenceException thrown = await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => store.CreateAsync("session-wrap-1", new TestSession(), new FakeSessionAgent()));

        Assert.IsType<MongoConnectionException>(thrown.InnerException);
    }

    [Fact]
    public async Task SetAsync_WhenUpdateFailsWithMongoException_WrapsAsPersistenceExceptionPreservingInner()
    {
        var state = new SessionCollectionState { UpdateException = ConnectionFailure() };
        var store = CreateStore(state);

        MongoDBPersistenceException thrown = await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => store.SetAsync("session-wrap-2", new TestSession(), new FakeSessionAgent()));

        Assert.IsType<MongoConnectionException>(thrown.InnerException);
    }

    [Fact]
    public async Task DeleteAsync_WhenDeleteFailsWithMongoException_WrapsAsPersistenceExceptionPreservingInner()
    {
        var state = new SessionCollectionState { DeleteException = ConnectionFailure() };
        var store = CreateStore(state);

        MongoDBPersistenceException thrown = await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => store.DeleteAsync("session-wrap-3"));

        Assert.IsType<MongoConnectionException>(thrown.InnerException);
    }

    private sealed class MutableClock(DateTimeOffset initial)
    {
        public DateTimeOffset Now { get; set; } = initial;

        public DateTimeOffset Read() => Now;
    }

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
