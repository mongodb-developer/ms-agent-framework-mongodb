using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

#pragma warning disable MAAI001

namespace MongoDB.AgentFramework.Tests.History;

public sealed class MongoDBChatHistoryBehaviorTests
{
    [Fact]
    public async Task MessagesRoundTripLosslesslyInDeterministicOrder()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);
        var messages = new[]
        {
            new ChatMessage(
                ChatRole.User,
                [
                    new TextContent("show weather"),
                    new UriContent("https://example.invalid/radar.png", "image/png")
                    {
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            ["content-extra"] = new Dictionary<string, object?> { ["nested"] = true },
                        },
                    },
                ])
            {
                AuthorName = "Ada",
                MessageId = "message-user",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["trace"] = new Dictionary<string, object?> { ["attempt"] = 2 },
                    ["unknown_future_property"] = "preserve-me",
                },
            },
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("call-1", "weather", new Dictionary<string, object?> { ["city"] = "London" })])
            {
                MessageId = "message-call",
            },
            new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent("call-1", new Dictionary<string, object> { ["temperature"] = 19 })])
            {
                MessageId = "message-result",
            },
        };

        await provider.SaveMessagesAsync("session", messages);
        IReadOnlyList<ChatMessage> restored = await provider.GetMessagesAsync("session");

        Assert.Equal(["message-user", "message-call", "message-result"], restored.Select(m => m.MessageId));
        Assert.IsType<FunctionCallContent>(restored[1].Contents.Single());
        Assert.IsType<FunctionResultContent>(restored[2].Contents.Single());
        Assert.Equal(
            "preserve-me",
            restored[0].AdditionalProperties!["unknown_future_property"]?.ToString());
        Assert.Equal([1L, 2L, 3L], MessageDocuments(state).Select(d => d["sequence"].AsInt64));
        Assert.All(MessageDocuments(state), document =>
        {
            Assert.Equal(1, document["schema_version"]);
            Assert.Equal(1, document["framework_version"]);
            Assert.IsType<BsonDocument>(document["message"]);
        });
    }

    [Fact]
    public async Task PublicFrameworkContentTypesRemainGroupedAndPolymorphic()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);
        var approvedCall = new FunctionCallContent("approval-call", "remove", null);
        AIContent[] contents =
        [
            new TextContent("text"),
            new TextReasoningContent("reasoning"),
            new DataContent("data:text/plain;base64,dGVzdA==", "text/plain"),
            new UriContent("https://example.invalid/file", "text/plain"),
            new ErrorContent("error"),
            new FunctionCallContent("call", "tool", null),
            new FunctionResultContent("call", new Dictionary<string, object?> { ["ok"] = true }),
            new UsageContent(),
            new HostedFileContent("file-id"),
            new HostedVectorStoreContent("store-id"),
            new CodeInterpreterToolCallContent("code-call"),
            new CodeInterpreterToolResultContent("code-call"),
            new ImageGenerationToolCallContent("image-call"),
            new ImageGenerationToolResultContent("image-call"),
            new McpServerToolCallContent("mcp-call", "server", "tool"),
            new McpServerToolResultContent("mcp-call"),
            new WebSearchToolCallContent("search-call"),
            new WebSearchToolResultContent("search-call"),
            new ToolApprovalRequestContent("approval", approvedCall),
            new ToolApprovalResponseContent("approval", true, approvedCall),
        ];
        var message = new ChatMessage(ChatRole.Assistant, contents)
        {
            MessageId = "all-content",
        };

        await provider.SaveMessagesAsync("session", [message]);
        ChatMessage restored = Assert.Single(await provider.GetMessagesAsync("session"));

        Assert.Equal(
            contents.Select(content => content.GetType()),
            restored.Contents.Select(content => content.GetType()));
        Assert.Equal(contents.Length, restored.Contents.Count);
    }

    [Fact]
    public async Task LatestNIsScopedInMongoThenReturnedChronologically()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state, ValidOptions() with { MaxMessages = 2 });
        await provider.SaveMessagesAsync(
            "session",
            Enumerable.Range(0, 3).Select(index =>
                new ChatMessage(ChatRole.User, index.ToString()) { MessageId = $"m-{index}" }));

        IReadOnlyList<ChatMessage> messages = await provider.GetMessagesAsync("session");

        Assert.Equal(["m-1", "m-2"], messages.Select(m => m.MessageId));
        Assert.Equal(-1, state.LastFindSort!["sequence"]);
        Assert.Equal(2, state.LastFindLimit);
        Assert.Equal("app", state.LastFindFilter!["application_id"]);
        Assert.Equal("agent", state.LastFindFilter["agent_id"]);
        Assert.Equal("session", state.LastFindFilter["session_id"]);
    }

    [Fact]
    public async Task AgeAndRetentionAreAppliedServerSideAndToStoredEnvelope()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(
            state,
            ValidOptions() with
            {
                MaxAge = TimeSpan.FromDays(7),
                Retention = TimeSpan.FromDays(30),
            });
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "retained") { MessageId = "retained" }]);

        await provider.GetMessagesAsync("session");

        BsonDocument document = Assert.Single(MessageDocuments(state));
        Assert.True(document.Contains("expires_at"));
        Assert.True(state.LastFindFilter!["created_at"].AsBsonDocument.Contains("$gte"));
    }

    [Fact]
    public async Task StableBatchRetryIsIdempotent()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);
        ChatMessage[] messages =
        [
            new(ChatRole.User, "same") { MessageId = "stable-1" },
            new(ChatRole.Assistant, "response") { MessageId = "stable-2" },
        ];

        await provider.SaveMessagesAsync("session", messages);
        await provider.SaveMessagesAsync("session", messages);

        Assert.Equal(2, MessageDocuments(state).Count);
    }

    [Fact]
    public async Task DirectFallbackIdsAreReusedOnlyForFailedRetry()
    {
        var state = new HistoryCollectionState { InsertException = OfflineException() };
        var provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => provider.SaveMessagesAsync(
                "session",
                [new ChatMessage(ChatRole.User, "same")]));
        string failedId = state.InsertAttempts[0]["message_id"].AsString;

        state.InsertException = null;
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "same")]);
        string retryId = state.InsertAttempts[1]["message_id"].AsString;
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "same")]);
        string laterId = state.InsertAttempts[2]["message_id"].AsString;

        Assert.Equal(failedId, retryId);
        Assert.NotEqual(retryId, laterId);
        Assert.Equal(2, MessageDocuments(state).Count);
    }

    [Fact]
    public async Task SeparateIdenticalSuccessfulTurnsReceiveDistinctIds()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);

        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "same")]);
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "same")]);

        Assert.Equal(2, state.InsertAttempts.Count);
        Assert.NotEqual(
            state.InsertAttempts[0]["message_id"],
            state.InsertAttempts[1]["message_id"]);
        Assert.Equal(2, MessageDocuments(state).Count);
    }

    [Fact]
    public async Task FrameworkRetrySurvivesSessionSerializationAndProviderRecreation()
    {
        var state = new HistoryCollectionState { InsertException = OfflineException() };
        MongoDBChatHistoryProvider firstProvider = CreateProvider(state);
        var session = new TestSession();
        session.StateBag.SetValue("unrelated", new { value = 42 });

        await Assert.ThrowsAsync<MongoDBPersistenceException>(
            async () => await firstProvider.InvokedAsync(
                Invoked(session, "retry me"),
                default));
        string failedId = state.InsertAttempts[0]["message_id"].AsString;
        Assert.Single(firstProvider.StateKeys);

        var restored = new TestSession(
            AgentSessionStateBag.Deserialize(session.StateBag.Serialize()));
        state.InsertException = null;
        MongoDBChatHistoryProvider recreatedProvider = CreateProvider(state);
        await recreatedProvider.InvokedAsync(Invoked(restored, "retry me"), default);

        Assert.Equal(failedId, state.InsertAttempts[1]["message_id"].AsString);
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
    public async Task InFlightFrameworkStateRecoversAfterProviderRecreation()
    {
        var state = new HistoryCollectionState();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int insertNumber = 0;
        state.InsertHandler = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref insertNumber) == 1)
            {
                firstStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        MongoDBChatHistoryProvider firstProvider = CreateProvider(state);
        var original = new TestSession();
        using var cancellation = new CancellationTokenSource();
        Task firstAttempt = firstProvider.InvokedAsync(
            Invoked(original, "retry me"),
            cancellation.Token).AsTask();
        await firstStarted.Task;
        string inFlightId = state.InsertAttempts[0]["message_id"].AsString;

        var restored = new TestSession(
            AgentSessionStateBag.Deserialize(original.StateBag.Serialize()));
        MongoDBChatHistoryProvider recreatedProvider = CreateProvider(state);
        await recreatedProvider.InvokedAsync(Invoked(restored, "retry me"), default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstAttempt);

        Assert.Equal(inFlightId, state.InsertAttempts[1]["message_id"].AsString);
        Assert.False(restored.StateBag.TryGetValue<Dictionary<string, object>>(
            recreatedProvider.StateKeys.Single(),
            out _));
    }

    [Fact]
    public async Task CancelledFrameworkAttemptReusesThenRetiresFallbackId()
    {
        var state = new HistoryCollectionState();
        bool cancel = true;
        state.InsertHandler = (_, cancellationToken) =>
        {
            if (cancel)
            {
                cancel = false;
                return Task.FromException(new OperationCanceledException(cancellationToken));
            }

            return Task.CompletedTask;
        };
        MongoDBChatHistoryProvider provider = CreateProvider(state);
        var session = new TestSession();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await provider.InvokedAsync(Invoked(session, "same"), default));
        string cancelledId = state.InsertAttempts[0]["message_id"].AsString;
        await provider.InvokedAsync(Invoked(session, "same"), default);
        string retryId = state.InsertAttempts[1]["message_id"].AsString;
        await provider.InvokedAsync(Invoked(session, "same"), default);
        string laterId = state.InsertAttempts[2]["message_id"].AsString;

        Assert.Equal(cancelledId, retryId);
        Assert.NotEqual(retryId, laterId);
    }

    [Fact]
    public async Task ConcurrentIdenticalFrameworkAttemptsUseDistinctIds()
    {
        var state = new HistoryCollectionState();
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        state.InsertHandler = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            await bothStarted.Task.WaitAsync(cancellationToken);
        };
        MongoDBChatHistoryProvider provider = CreateProvider(state);
        var session = new TestSession();

        await Task.WhenAll(
            provider.InvokedAsync(Invoked(session, "same"), default).AsTask(),
            provider.InvokedAsync(Invoked(session, "same"), default).AsTask());

        Assert.Equal(2, state.InsertAttempts.Count);
        Assert.NotEqual(
            state.InsertAttempts[0]["message_id"],
            state.InsertAttempts[1]["message_id"]);
        Assert.False(session.StateBag.TryGetValue<Dictionary<string, object>>(
            provider.StateKeys.Single(),
            out _));
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(1, true)]
    public async Task FrameworkRejectsUnsupportedOrMalformedRetryState(
        int version,
        bool malformed)
    {
        MongoDBChatHistoryProvider provider = CreateProvider(new HistoryCollectionState());
        var session = new TestSession();
        session.StateBag.SetValue(
            provider.StateKeys.Single(),
            malformed
                ? new { Version = version, Batches = new[] { "invalid" } }
                : (object)new { Version = version, Batches = new { } });
        session = new TestSession(
            AgentSessionStateBag.Deserialize(session.StateBag.Serialize()));

        MongoDBConfigurationException exception =
            await Assert.ThrowsAsync<MongoDBConfigurationException>(
                async () => await provider.InvokedAsync(
                    Invoked(session, "retry me"),
                    default));

        Assert.Contains("migration", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, session.StateBag.Count);
    }

    [Fact]
    public async Task CompatibleDuplicateKeyRaceConvergesAndRetiresFallbackId()
    {
        var state = new HistoryCollectionState();
        bool race = true;
        state.InsertHandler = (document, _) =>
        {
            if (race)
            {
                race = false;
                state.Documents.Add(document);
                return Task.FromException(DuplicateKeyException());
            }

            return Task.CompletedTask;
        };
        var provider = CreateProvider(state);

        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "same")]);
        string convergedId = state.InsertAttempts[0]["message_id"].AsString;
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "same")]);

        Assert.NotEqual(convergedId, state.InsertAttempts[1]["message_id"].AsString);
        Assert.Equal(2, MessageDocuments(state).Count);
    }

    [Fact]
    public async Task IncompatibleDuplicateKeyRaceFailsAndRetiresFallbackId()
    {
        var state = new HistoryCollectionState();
        state.InsertHandler = (document, _) =>
        {
            BsonDocument incompatible = document.DeepClone().AsBsonDocument;
            incompatible["message"].AsBsonDocument["role"] = "assistant";
            state.Documents.Add(incompatible);
            return Task.FromException(DuplicateKeyException());
        };
        var provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => provider.SaveMessagesAsync(
                "session",
                [new ChatMessage(ChatRole.User, "same")]));
        string incompatibleId = state.InsertAttempts[0]["message_id"].AsString;

        state.InsertHandler = null;
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "same")]);

        Assert.NotEqual(incompatibleId, state.InsertAttempts[1]["message_id"].AsString);
    }

    [Fact]
    public async Task UnauthorizedSessionIsRejectedBeforeMongoDbAccess()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);

        await Assert.ThrowsAsync<MongoDBConfigurationException>(
            () => provider.GetMessagesAsync("other"));
        await Assert.ThrowsAsync<MongoDBConfigurationException>(
            () => provider.SaveMessagesAsync("other", [new ChatMessage(ChatRole.User, "no")]));
        await Assert.ThrowsAsync<MongoDBConfigurationException>(
            () => provider.ClearMessagesAsync("other"));

        Assert.Equal(0, state.OperationCount);
    }

    [Fact]
    public async Task ClearDeletesOnlyAuthorizedMessagesAndSequence()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "delete") { MessageId = "delete-me" }]);
        state.Documents.Add(
            new BsonDocument
            {
                { "_id", "other" },
                { "_kind", "message" },
                { "application_id", "other-app" },
                { "agent_id", "agent" },
                { "session_id", "session" },
            });

        long count = await provider.ClearMessagesAsync("session");

        Assert.Equal(1, count);
        Assert.Single(state.Documents);
        Assert.Equal("other", state.Documents[0]["_id"]);
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("framework_version")]
    public async Task UnknownVersionsFailWithMigrationGuidance(string versionField)
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "hello") { MessageId = "m-1" }]);
        MessageDocuments(state)[0][versionField] = 99;

        MongoDBMappingException exception = await Assert.ThrowsAsync<MongoDBMappingException>(
            () => provider.GetMessagesAsync("session"));

        Assert.Contains("migration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegularIndexesAreProvisionedOnlyExplicitly()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(
            state,
            ValidOptions() with { Retention = TimeSpan.FromDays(30) });
        Assert.Empty(state.CreatedIndexes);

        IReadOnlyList<string> names = await provider.EnsureIndexesAsync();

        Assert.Equal(
            [
                "history_scoped_message_unique",
                "history_scoped_sequence",
                "history_expiration_ttl",
            ],
            names);
        Assert.True(state.CreatedIndexes[0].Options.Unique);
        Assert.Equal(TimeSpan.Zero, state.CreatedIndexes[2].Options.ExpireAfter);
        await provider.ValidateIndexesAsync();
    }

    [Fact]
    public async Task ConcurrentBatchesReceiveUniqueMonotonicSequences()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);

        await Task.WhenAll(
            provider.SaveMessagesAsync(
                "session",
                [
                    new ChatMessage(ChatRole.User, "a") { MessageId = "a" },
                    new ChatMessage(ChatRole.Assistant, "b") { MessageId = "b" },
                ]),
            provider.SaveMessagesAsync(
                "session",
                [
                    new ChatMessage(ChatRole.User, "c") { MessageId = "c" },
                    new ChatMessage(ChatRole.Assistant, "d") { MessageId = "d" },
                ]));

        Assert.Equal([1L, 2L, 3L, 4L], MessageDocuments(state).Select(d => d["sequence"]).Select(v => v.AsInt64).Order());
    }

    [Fact]
    public async Task BaseProviderOwnsFilteringMergingAndSourceAttribution()
    {
        var state = new HistoryCollectionState();
        var provider = CreateProvider(state);
        await provider.SaveMessagesAsync(
            "session",
            [new ChatMessage(ChatRole.User, "old") { MessageId = "old" }]);
        var session = new TestSession();
        var input = new ChatMessage(ChatRole.User, "new") { MessageId = "new" };

        ChatMessage[] request = (await provider.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(new StubAgent(), session, [input]),
            default)).ToArray();

        Assert.Equal(["old", "new"], request.Select(message => message.MessageId));
        Assert.Equal(
            AgentRequestMessageSourceType.ChatHistory,
            request[0].GetAgentRequestMessageSourceType());

        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(
                new StubAgent(),
                session,
                request,
                [new ChatMessage(ChatRole.Assistant, "answer") { MessageId = "answer" }]),
            default);

        Assert.Equal(["old", "new", "answer"], MessageDocuments(state).Select(d => d["message_id"].AsString));
    }

    [Fact]
    public async Task CancellationAndStableOperationalErrorsPropagate()
    {
        var state = new HistoryCollectionState
        {
            Failure = new MongoConnectionException(
                new ConnectionId(
                    new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                "offline"),
        };
        var provider = CreateProvider(state);

        MongoDBRetrievalException retrieval = await Assert.ThrowsAsync<MongoDBRetrievalException>(
            () => provider.GetMessagesAsync("session"));
        Assert.IsType<MongoConnectionException>(retrieval.InnerException);

        MongoDBPersistenceException persistence =
            await Assert.ThrowsAsync<MongoDBPersistenceException>(
                () => provider.SaveMessagesAsync(
                    "session",
                    [new ChatMessage(ChatRole.User, "message")]));
        Assert.IsType<MongoConnectionException>(persistence.InnerException);

        state.Failure = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetMessagesAsync("session"));

        state.Failure = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetMessagesAsync("session", cancellation.Token));
    }

    [Fact]
    public async Task ConnectionStringClientIsOwnedAndInjectedCollectionIsNot()
    {
        var injected = CreateProvider(new HistoryCollectionState());
        var owned = new MongoDBChatHistoryProvider(
            "mongodb://localhost:27017",
            "history_test",
            "messages",
            ValidOptions());

        Assert.False(injected.OwnsClient);
        Assert.True(owned.OwnsClient);
        await injected.DisposeAsync();
        await owned.DisposeAsync();
        await owned.DisposeAsync();
    }

    private static MongoDBChatHistoryProvider CreateProvider(
        HistoryCollectionState state,
        MongoDBChatHistoryProviderOptions? options = null) =>
        new(HistoryCollectionProxy.Create(state), options ?? ValidOptions());

    private static MongoDBChatHistoryProviderOptions ValidOptions() =>
        new()
        {
            ApplicationId = "app",
            AgentId = "agent",
            SessionId = "session",
        };

    private static ChatHistoryProvider.InvokedContext Invoked(
        AgentSession session,
        string text) =>
        new(
            new StubAgent(),
            session,
            [new ChatMessage(ChatRole.User, text)],
            []);

    private static MongoConnectionException OfflineException() =>
        new(
            new ConnectionId(
                new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "offline");

    private static MongoCommandException DuplicateKeyException()
    {
        var connectionId = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        return new MongoCommandException(
            connectionId,
            "insert",
            new BsonDocument(),
            new BsonDocument
            {
                { "ok", 0 },
                { "code", 11000 },
                { "errmsg", "duplicate" },
            });
    }

    private static List<BsonDocument> MessageDocuments(HistoryCollectionState state) =>
        state.Documents.Where(document => document["_kind"] == "message").ToList();

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
