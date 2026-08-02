using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

#pragma warning disable MAAI001

namespace MongoDB.AgentFramework.Tests.Memory;

public sealed class MongoDBMemoryBehaviorTests
{
    [Fact]
    public async Task StoreBatchesEmbeddingsAndUsesStableRetryIds()
    {
        var state = new MemoryCollectionState();
        var embeddings = new RecordingEmbeddingGenerator();
        MongoDBMemoryProvider provider = CreateProvider(state, embeddings);
        ChatMessage[] messages =
        [
            new(ChatRole.User, "blue preference"),
            new(ChatRole.Assistant, "remembered"),
        ];

        state.InsertException = new MongoConnectionException(
            new ConnectionId(
                new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "offline");
        await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => provider.StoreAsync(messages, new MongoDBMemoryScope(userId: "u")));
        string[] failedIds = state.InsertAttempts[0]
            .Select(document => document["_id"].AsString).ToArray();
        state.InsertException = null;
        await provider.StoreAsync(messages, new MongoDBMemoryScope(userId: "u"));
        string[] retryIds = state.InsertAttempts[1]
            .Select(document => document["_id"].AsString).ToArray();
        state.Inserted.Clear();
        await provider.StoreAsync(messages, new MongoDBMemoryScope(userId: "u"));
        string[] nextSuccessIds = state.InsertAttempts[2]
            .Select(document => document["_id"].AsString).ToArray();

        Assert.Equal(3, embeddings.Calls.Count);
        Assert.All(embeddings.Calls, call => Assert.Equal(2, call.Length));
        Assert.Equal(failedIds, retryIds);
        Assert.NotEqual(retryIds, nextSuccessIds);
        Assert.All(state.Inserted, document => Assert.Equal("u", document["user_id"]));
    }

    [Fact]
    public async Task ConcurrentIdenticalDirectStoresUseIsolatedIds()
    {
        var state = new MemoryCollectionState();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        state.InsertHandler = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            await bothStarted.Task.WaitAsync(cancellationToken);
        };
        MongoDBMemoryProvider provider = CreateProvider(state);
        ChatMessage[] messages = [new(ChatRole.User, "same")];
        MongoDBMemoryScope scope = new(userId: "user");

        await Task.WhenAll(
            provider.StoreAsync(messages, scope),
            provider.StoreAsync(messages, scope));

        Assert.Equal(2, state.InsertAttempts.Count);
        Assert.NotEqual(
            state.InsertAttempts[0][0]["_id"],
            state.InsertAttempts[1][0]["_id"]);
        Assert.Equal(2, state.Inserted.Count);
    }

    [Theory]
    [InlineData(false, "numCandidates")]
    [InlineData(true, "exact")]
    public async Task SearchPlacesScopeInsideAnnOrEnnStage(bool exact, string option)
    {
        var state = new MemoryCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "m1" }, { "role", "user" }, { "content", "blue" },
                    { "score", 0.9 }, { "session_id", "s" },
                },
            ],
        };
        MongoDBMemoryProvider provider = CreateProvider(state);

        IReadOnlyList<MongoDBMemorySearchResult> results = await provider.SearchAsync(
            "blue",
            new MongoDBMemoryScope("app", "agent", "user", "session"),
            exact: exact);

        BsonDocument vector = state.AggregateStages[0]["$vectorSearch"].AsBsonDocument;
        Assert.True(vector.Contains(option));
        Assert.Equal(
            BsonDocument.Parse(
                """{"application_id":"app","agent_id":"agent","user_id":"user","session_id":"session"}"""),
            vector["filter"].AsBsonDocument);
        Assert.Equal("m1", Assert.Single(results).MemoryId);
    }

    [Fact]
    public async Task LifecycleDeletionAlwaysCombinesIdAndAuthorizationScope()
    {
        var state = new MemoryCollectionState();
        MongoDBMemoryProvider provider = CreateProvider(state);

        long deleted = await provider.DeleteByIdAsync(
            "m1",
            new MongoDBMemoryScope(applicationId: "app", userId: "user"));

        Assert.Equal(1, deleted);
        Assert.Equal("m1", state.DeleteFilter!["_id"]);
        Assert.Equal("app", state.DeleteFilter["application_id"]);
        Assert.Equal("user", state.DeleteFilter["user_id"]);
    }

    [Fact]
    public async Task LifecycleDeletionRejectsUnacknowledgedResult()
    {
        var state = new MemoryCollectionState { DeleteAcknowledged = false };
        MongoDBMemoryProvider provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => provider.DeleteByIdAsync(
                "m1",
                new MongoDBMemoryScope(applicationId: "app", userId: "user")));
    }

    [Fact]
    public async Task StoreWritesConfiguredNestedVectorPath()
    {
        var state = new MemoryCollectionState();
        var provider = new MongoDBMemoryProvider(
            MemoryCollectionProxy.Create(state),
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope(userId: "user")),
            new MongoDBMemoryProviderOptions { VectorFieldName = "vectors.content" });

        await provider.StoreAsync(
            [new ChatMessage(ChatRole.User, "blue")],
            new MongoDBMemoryScope(userId: "user"));

        BsonDocument document = Assert.Single(state.Inserted);
        Assert.Equal(
            new BsonArray(new float[] { 1, 0, 0 }),
            document["vectors"]["content"]);
        Assert.False(document.Contains("vectors.content"));
    }

    [Fact]
    public async Task ListReturnsBoundedContentFreeMetadata()
    {
        var state = new MemoryCollectionState
        {
            ListedDocuments =
            [
                new BsonDocument
                {
                    { "_id", "m1" }, { "role", "user" },
                    { "created_at", DateTime.UtcNow },
                    { "application_id", "app" }, { "user_id", "user" },
                },
            ],
        };
        MongoDBMemoryProvider provider = CreateProvider(state);

        MongoDBMemoryMetadataPage page = await provider.ListAsync(
            new MongoDBMemoryScope(applicationId: "app", userId: "user"));

        MongoDBMemoryMetadata item = Assert.Single(page.Items);
        Assert.Equal("m1", item.MemoryId);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task CancellationFromEmbeddingPropagates()
    {
        var embeddings = new RecordingEmbeddingGenerator { Cancel = true };
        MongoDBMemoryProvider provider = CreateProvider(
            new MemoryCollectionState(),
            embeddings);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchAsync(
                "blue",
                new MongoDBMemoryScope(userId: "user")));
    }

    [Fact]
    public async Task RetrievalDeadlineUsesStableTimeoutError()
    {
        var provider = new MongoDBMemoryProvider(
            MemoryCollectionProxy.Create(new MemoryCollectionState()),
            new RecordingEmbeddingGenerator { Delay = TimeSpan.FromSeconds(1) },
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope(userId: "user")),
            new MongoDBMemoryProviderOptions
            {
                RetrievalTimeout = TimeSpan.FromMilliseconds(10),
            });

        MongoDBTimeoutException exception =
            await Assert.ThrowsAsync<MongoDBTimeoutException>(
                () => provider.SearchAsync(
                    "blue",
                    new MongoDBMemoryScope(userId: "user")));

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task InvokingAndInvokedUseFrameworkPublicLifecycle()
    {
        var state = new MemoryCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "m1" }, { "role", "user" },
                    { "content", "earlier blue" }, { "score", 1.0 },
                },
            ],
        };
        MongoDBMemoryProvider provider = CreateProvider(state);
        var agent = new StubAgent();
        AIContext supplied = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                agent,
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "blue")] }),
            default);
        await provider.InvokedAsync(
            new AIContextProvider.InvokedContext(
                agent,
                null,
                [new ChatMessage(ChatRole.User, "new input")],
                [new ChatMessage(ChatRole.Assistant, "new response")]),
            default);

        Assert.Contains("Relevant memories", supplied.Instructions);
        Assert.Contains(
            supplied.Messages!,
            message => message.Text == "earlier blue");
        Assert.Equal(2, state.Inserted.Count);
    }

    [Fact]
    public async Task FrameworkRetrievalFailsOpenButCancellationDoesNot()
    {
        var state = new MemoryCollectionState
        {
            AggregateException = new MongoConnectionException(
                new ConnectionId(
                    new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                "offline"),
        };
        MongoDBMemoryProvider provider = CreateProvider(state);

        AIContext context = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "query")] }),
            default);

        Assert.DoesNotContain(
            context.Messages ?? [],
            message => message.AdditionalProperties?.ContainsKey("_memory_id") is true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameworkPersistencePolicyIsConfigurable(bool failFast)
    {
        var state = new MemoryCollectionState
        {
            InsertException = new MongoConnectionException(
                new ConnectionId(
                    new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                "offline"),
        };
        var provider = new MongoDBMemoryProvider(
            MemoryCollectionProxy.Create(state),
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope(userId: "user")),
            new MongoDBMemoryProviderOptions { PersistenceFailFast = failFast });
        var invoked = new AIContextProvider.InvokedContext(
            new StubAgent(),
            null,
            [new ChatMessage(ChatRole.User, "new input")],
            [new ChatMessage(ChatRole.Assistant, "new response")]);

        if (failFast)
        {
            await Assert.ThrowsAsync<MongoDBPersistenceException>(
                async () => await provider.InvokedAsync(invoked, default));
        }
        else
        {
            await provider.InvokedAsync(invoked, default);
        }
    }

    [Fact]
    public async Task FrameworkRetryStateSurvivesSessionSerializationAndProviderRecreation()
    {
        var state = new MemoryCollectionState
        {
            InsertException = OfflineException(),
        };
        MongoDBMemoryProvider firstProvider = CreateProvider(state);
        var session = new TestSession();
        session.StateBag.SetValue("unrelated", new { value = 42 });
        var invoked = new AIContextProvider.InvokedContext(
            new StubAgent(),
            session,
            [new ChatMessage(ChatRole.User, "retry me")],
            []);

        await firstProvider.InvokedAsync(invoked, default);
        string failedId = state.InsertAttempts[0][0]["_id"].AsString;
        Assert.Single(firstProvider.StateKeys);

        JsonElement serialized = session.StateBag.Serialize();
        var restored = new TestSession(AgentSessionStateBag.Deserialize(serialized));
        state.InsertException = null;
        MongoDBMemoryProvider recreatedProvider = CreateProvider(state);
        await recreatedProvider.InvokedAsync(
            new AIContextProvider.InvokedContext(
                new StubAgent(),
                restored,
                [new ChatMessage(ChatRole.User, "retry me")],
                []),
            default);

        Assert.Equal(failedId, state.InsertAttempts[1][0]["_id"].AsString);
        Assert.True(restored.StateBag.TryGetValue(
            "unrelated",
            out Dictionary<string, int>? unrelated));
        Assert.Equal(42, unrelated!["value"]);
        Assert.False(restored.StateBag.TryGetValue<Dictionary<string, object>>(
            recreatedProvider.StateKeys.Single(),
            out _));
        Assert.Equal(1, restored.StateBag.Count);
    }

    [Fact]
    public async Task ConcurrentFrameworkAttemptsUseIsolatedSessionState()
    {
        var state = new MemoryCollectionState();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        state.InsertHandler = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            await bothStarted.Task.WaitAsync(cancellationToken);
        };
        MongoDBMemoryProvider provider = CreateProvider(state);
        var session = new TestSession();
        var invoked = new AIContextProvider.InvokedContext(
            new StubAgent(),
            session,
            [new ChatMessage(ChatRole.User, "same")],
            []);

        await Task.WhenAll(
            provider.InvokedAsync(invoked, default).AsTask(),
            provider.InvokedAsync(invoked, default).AsTask());

        Assert.Equal(2, state.InsertAttempts.Count);
        Assert.NotEqual(
            state.InsertAttempts[0][0]["_id"],
            state.InsertAttempts[1][0]["_id"]);
        Assert.False(session.StateBag.TryGetValue<Dictionary<string, object>>(
            provider.StateKeys.Single(),
            out _));
        Assert.Equal(0, session.StateBag.Count);
    }

    [Fact]
    public async Task FrameworkRejectsUnsupportedRetryStateWithMigrationGuidance()
    {
        MongoDBMemoryProvider provider = CreateProvider(new MemoryCollectionState());
        var session = new TestSession();
        session.StateBag.SetValue(
            provider.StateKeys.Single(),
            new { Version = 99, Batches = new { } });
        session = new TestSession(
            AgentSessionStateBag.Deserialize(session.StateBag.Serialize()));

        MongoDBConfigurationException exception =
            await Assert.ThrowsAsync<MongoDBConfigurationException>(
                async () => await provider.InvokedAsync(
                    new AIContextProvider.InvokedContext(
                        new StubAgent(),
                        session,
                        [new ChatMessage(ChatRole.User, "retry me")],
                        []),
                    default));

        Assert.Contains("migration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FrameworkRejectsMalformedRetryStateWithoutDiscardingIt()
    {
        MongoDBMemoryProvider provider = CreateProvider(new MemoryCollectionState());
        var session = new TestSession();
        session.StateBag.SetValue(
            provider.StateKeys.Single(),
            new { Version = 1, Batches = new[] { "invalid" } });
        session = new TestSession(
            AgentSessionStateBag.Deserialize(session.StateBag.Serialize()));

        MongoDBConfigurationException exception =
            await Assert.ThrowsAsync<MongoDBConfigurationException>(
                async () => await provider.InvokedAsync(
                    new AIContextProvider.InvokedContext(
                        new StubAgent(),
                        session,
                        [new ChatMessage(ChatRole.User, "retry me")],
                        []),
                    default));

        Assert.Contains(provider.StateKeys.Single(), exception.Message);
        Assert.Equal(1, session.StateBag.Count);
    }

#pragma warning restore MAAI001

    private static MongoDBMemoryProvider CreateProvider(
        MemoryCollectionState state,
        RecordingEmbeddingGenerator? embeddings = null) =>
        new(
            MemoryCollectionProxy.Create(state),
            embeddings ?? new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope(userId: "user")));

    private static MongoConnectionException OfflineException() =>
        new(
            new ConnectionId(
                new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "offline");

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

    private sealed class StubAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedSession,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
