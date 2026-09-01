using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Driver;
using System.Runtime.CompilerServices;
using System.Text.Json;

#pragma warning disable MAAI001

namespace MongoDB.AgentFramework.Tests.Persistence;

public sealed class MongoDBAgentSessionStoreIntegrationTests
{
    [MongoPersistenceIntegrationFact]
    [Trait("Category", "integration-persistence")]
    public async Task ExactReloadCasRetryIsolationTtlAndAuthorizedCleanup()
    {
        string uri = Environment.GetEnvironmentVariable("MONGODB_URI")!;
        string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")!;
        string collectionName = $"af_persistence_dotnet_test_{Guid.NewGuid():N}";
        using var client = new MongoClient(uri);
        IMongoCollection<MongoDB.Bson.BsonDocument> collection =
            client.GetDatabase(databaseName).GetCollection<MongoDB.Bson.BsonDocument>(collectionName);

        static MongoDBAgentSessionStoreOptions Options(string tenantId) =>
            new()
            {
                TenantId = tenantId,
                ApplicationId = "integration-persistence",
                AgentId = "persistence-agent",
                DefaultExpiration = TimeSpan.FromDays(1),
            };

        var store = new MongoDBAgentSessionStore(collection, Options("tenant-a"));
        var otherTenant = new MongoDBAgentSessionStore(collection, Options("tenant-b"));
        var agent = new IntegrationFakeAgent();
        try
        {
            await store.EnsureIndexesAsync();
            await store.ValidateIndexesAsync();

            var bag = new AgentSessionStateBag();
            bag.SetValue("greeting", "hello");
            bag.SetValue("unknown_future_field", (object)JsonDocument.Parse("""{"a":1}""").RootElement);
            MongoDBAgentSessionRecord created = await store.CreateAsync(
                "session-a",
                new IntegrationTestSession(bag),
                agent);

            MongoDBAgentSessionRecord? crossTenant = await otherTenant.GetAsync("session-a", agent);
            Assert.Null(crossTenant);

            // Retrying an identical CreateAsync call after real elapsed time should converge (not conflict) on
            // the originally persisted default expiry, without extending it.
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            MongoDBAgentSessionRecord createRetried = await store.CreateAsync(
                "session-a",
                new IntegrationTestSession(bag),
                agent);
            Assert.Equal(created.Version, createRetried.Version);
            Assert.Equal(created.ExpiresAt, createRetried.ExpiresAt);

            MongoDBAgentSessionRecord updated = await store.SetAsync(
                "session-a",
                new IntegrationTestSession(bag),
                agent,
                expectedVersion: created.Version);
            Assert.Equal("2", updated.Version);

            // Retrying the same CAS write with the stale expected version should converge, not conflict, even
            // though real elapsed time means a freshly recomputed default expiry would differ from what the
            // first successful attempt already persisted; the retry must not extend it.
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            MongoDBAgentSessionRecord retried = await store.SetAsync(
                "session-a",
                new IntegrationTestSession(bag),
                agent,
                expectedVersion: created.Version);
            Assert.Equal(updated.Version, retried.Version);
            Assert.Equal(updated.ExpiresAt, retried.ExpiresAt);

            MongoDBAgentSessionRecord? reloaded = await store.GetAsync("session-a", agent);
            Assert.NotNull(reloaded);
            Assert.Equal("hello", reloaded!.Session.StateBag.GetValue<string>("greeting"));
            Assert.NotNull(reloaded.ExpiresAt);

            MongoDBAgentSessionPage page = await store.ListAsync(10);
            Assert.Contains(page.Items, item => item.SessionId == "session-a");

            Assert.True(await store.DeleteAsync("session-a"));
            Assert.Null(await store.GetAsync("session-a", agent));
        }
        finally
        {
            Assert.StartsWith("af_persistence_dotnet_test_", collectionName);
            await client.GetDatabase(databaseName).DropCollectionAsync(collectionName);
            await store.DisposeAsync();
            await otherTenant.DisposeAsync();
        }
    }

    private sealed class IntegrationTestSession : AgentSession
    {
        public IntegrationTestSession()
        {
        }

        public IntegrationTestSession(AgentSessionStateBag stateBag)
            : base(stateBag)
        {
        }
    }

    private sealed class IntegrationFakeAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new IntegrationTestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(session.StateBag.Serialize());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedSession,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(
                new IntegrationTestSession(AgentSessionStateBag.Deserialize(serializedSession)));

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

    private sealed class MongoPersistenceIntegrationFactAttribute : FactAttribute
    {
        public MongoPersistenceIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_URI")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_DATABASE")))
            {
                Skip = "MONGODB_URI and MONGODB_DATABASE are required for integration-persistence.";
            }
        }
    }
}
