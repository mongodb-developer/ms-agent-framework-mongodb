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

namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class MongoDBRAGContextProviderTests
{
    [Fact]
    public async Task SuppliesAttributedToolMessagesForNonEmptyResults()
    {
        var state = new RAGCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "chunk-1" },
                    { "text", "Widgets ship in blue." },
                    { "_ragScore", 0.9 },
                    { "source", new BsonDocument { { "name", "Catalog" }, { "url", "https://example.test/c" } } },
                },
            ],
        };
        MongoDBRAGProvider provider = CreateProvider(state);
        var contextProvider = new MongoDBRAGContextProvider(provider);

        AIContext context = await contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "what color are widgets")] }),
            default);

        ChatMessage message = Assert.Single(
            context.Messages!,
            candidate => candidate.AdditionalProperties?.ContainsKey("_rag_id") is true);
        Assert.Equal(ChatRole.Tool, message.Role);
        Assert.Equal("Widgets ship in blue.", message.Text);
        Assert.Equal("chunk-1", message.AdditionalProperties!["_rag_id"]);
        Assert.Equal(0.9, message.AdditionalProperties!["_rag_score"]);
        Assert.Equal("Catalog", message.AdditionalProperties!["_rag_source_name"]);
        Assert.Equal("https://example.test/c", message.AdditionalProperties!["_rag_source_url"]);
        Assert.NotNull(context.Instructions);
        Assert.DoesNotContain("Widgets ship in blue.", context.Instructions);
    }

    [Fact]
    public async Task EmptyQueryShortCircuitsWithoutSearching()
    {
        var state = new RAGCollectionState();
        MongoDBRAGProvider provider = CreateProvider(state);
        var contextProvider = new MongoDBRAGContextProvider(provider);

        AIContext context = await contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "   ")] }),
            default);

        Assert.DoesNotContain(
            context.Messages ?? [],
            message => message.AdditionalProperties?.ContainsKey("_rag_id") is true);
        Assert.Null(context.Instructions);
        Assert.Empty(state.AggregateStages);
    }

    [Fact]
    public async Task EmptyResultsProduceAnEmptyContextWithoutInstructions()
    {
        var state = new RAGCollectionState { Results = [] };
        MongoDBRAGProvider provider = CreateProvider(state);
        var contextProvider = new MongoDBRAGContextProvider(provider);

        AIContext context = await contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "query")] }),
            default);

        Assert.DoesNotContain(
            context.Messages ?? [],
            message => message.AdditionalProperties?.ContainsKey("_rag_id") is true);
        Assert.Null(context.Instructions);
    }

    [Theory]
    [InlineData(typeof(MongoConnectionException))]
    public async Task RetrievalFailuresFailOpenToAnEmptyContext(Type _)
    {
        var state = new RAGCollectionState
        {
            AggregateException = new MongoConnectionException(
                new ConnectionId(
                    new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                "offline"),
        };
        MongoDBRAGProvider provider = CreateProvider(state);
        var contextProvider = new MongoDBRAGContextProvider(provider);

        AIContext context = await contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "query")] }),
            default);

        Assert.DoesNotContain(
            context.Messages ?? [],
            message => message.AdditionalProperties?.ContainsKey("_rag_id") is true);
    }

    [Fact]
    public async Task EmbeddingFailuresFailOpenToAnEmptyContext()
    {
        var embeddings = new RecordingEmbeddingGenerator { Dimensions = 2 };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), embeddings);
        var contextProvider = new MongoDBRAGContextProvider(provider);

        AIContext context = await contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "query")] }),
            default);

        Assert.DoesNotContain(
            context.Messages ?? [],
            message => message.AdditionalProperties?.ContainsKey("_rag_id") is true);
    }

    [Fact]
    public async Task TimeoutFailuresFailOpenToAnEmptyContext()
    {
        var embeddings = new RecordingEmbeddingGenerator { Delay = TimeSpan.FromSeconds(5) };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            RetrievalTimeout = TimeSpan.FromMilliseconds(20),
        };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), embeddings, options);
        var contextProvider = new MongoDBRAGContextProvider(provider);

        AIContext context = await contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "query")] }),
            default);

        Assert.DoesNotContain(
            context.Messages ?? [],
            message => message.AdditionalProperties?.ContainsKey("_rag_id") is true);
    }

    [Fact]
    public async Task CapabilityErrorsPropagateRatherThanFailingOpen()
    {
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), options: options);
        var contextProvider = new MongoDBRAGContextProvider(provider);

        await Assert.ThrowsAsync<MongoDBCapabilityException>(() => contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "query")] }),
            default).AsTask());
    }

    [Fact]
    public async Task CancellationPropagatesRatherThanFailingOpen()
    {
        var embeddings = new RecordingEmbeddingGenerator { Delay = TimeSpan.FromSeconds(5) };
        MongoDBRAGProvider provider = CreateProvider(new RAGCollectionState(), embeddings);
        var contextProvider = new MongoDBRAGContextProvider(provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "query")] }),
            cancellation.Token).AsTask());
    }

    [Fact]
    public async Task RecentMessageWindowLimitsQueryConstruction()
    {
        var state = new RAGCollectionState();
        var embeddings = new RecordingEmbeddingGenerator();
        MongoDBRAGProvider provider = CreateProvider(state, embeddings);
        var contextProvider = new MongoDBRAGContextProvider(
            provider,
            new MongoDBRAGContextProviderOptions { MaxRecentMessages = 1 });

        await contextProvider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new StubAgent(),
                null,
                new AIContext
                {
                    Messages =
                    [
                        new ChatMessage(ChatRole.User, "first"),
                        new ChatMessage(ChatRole.User, "second"),
                    ],
                }),
            default);

        Assert.Equal(["second"], Assert.Single(embeddings.Calls));
    }

    private static MongoDBRAGProvider CreateProvider(
        RAGCollectionState state,
        RecordingEmbeddingGenerator? embeddings = null,
        MongoDBRAGProviderOptions? options = null) =>
        new(
            RAGCollectionProxy.Create(state),
            embeddings ?? new RecordingEmbeddingGenerator(),
            3,
            options ?? new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn });

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
