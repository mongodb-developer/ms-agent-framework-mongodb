using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MongoDB.AgentFramework;

/// <summary>
/// A before-invoke Agent Framework context adapter that composes a <see cref="MongoDBRAGProvider"/> to supply
/// retrieved chunks as attributed <see cref="ChatRole.Tool"/> context messages.
/// </summary>
/// <remarks>
/// This adapter is built directly on the public <see cref="AIContextProvider"/> seam rather than composed from
/// <c>TextSearchProvider</c>: the currently resolved <c>Microsoft.Agents.AI.Abstractions</c> package (1.13.0, the
/// version this project's dependency range actually locks to, confirmed via <c>project.assets.json</c>) does not
/// expose a <c>TextSearchProvider</c> type. Per <c>docs/spec/features/rag.md</c>, a dedicated adapter is the
/// documented fallback when composition is not available against installed APIs, and it preserves the complete
/// <see cref="MongoDBRAGResult"/> information (score, source, metadata) through its own message-attribution path
/// instead of reducing results to a narrower shape.
/// </remarks>
public sealed class MongoDBRAGContextProvider : AIContextProvider
{
    private readonly MongoDBRAGProvider _provider;
    private readonly MongoDBRAGContextProviderOptions _options;
    private readonly ILogger<MongoDBRAGContextProvider> _logger;

    /// <summary>
    /// Creates an adapter that composes <paramref name="provider"/>. The adapter does not own or dispose the
    /// composed provider; the caller that constructed it retains that responsibility.
    /// </summary>
    public MongoDBRAGContextProvider(
        MongoDBRAGProvider provider,
        MongoDBRAGContextProviderOptions? options = null,
        ILogger<MongoDBRAGContextProvider>? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = (options ?? new MongoDBRAGContextProviderOptions()).Copy();
        _logger = logger ?? NullLogger<MongoDBRAGContextProvider>.Instance;
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => [];

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        IEnumerable<ChatMessage> messages = context.AIContext.Messages ?? [];
        if (_options.MaxRecentMessages is { } window)
        {
            messages = messages.TakeLast(window);
        }

        string query = string.Join(
            " ",
            messages
                .Select(static message => message.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(query))
        {
            return new AIContext();
        }

        try
        {
            IReadOnlyList<MongoDBRAGResult> results = await _provider.SearchAsync(
                query,
                cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
            {
                return new AIContext();
            }

            return new AIContext
            {
                Instructions = _options.Instructions,
                Messages = results.Select(MapContextMessage),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoDBRetrievalException)
        {
            _logger.LogWarning("MongoDB RAG adapter retrieval failed.");
            return new AIContext();
        }
        catch (MongoDBEmbeddingException)
        {
            _logger.LogWarning("MongoDB RAG adapter retrieval failed.");
            return new AIContext();
        }
        catch (MongoDBTimeoutException)
        {
            _logger.LogWarning("MongoDB RAG adapter retrieval failed.");
            return new AIContext();
        }
    }

    private static ChatMessage MapContextMessage(MongoDBRAGResult result) =>
        new(ChatRole.Tool, result.Text)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["_rag_id"] = result.Id,
                ["_rag_score"] = result.Score,
                ["_rag_source_name"] = result.SourceName,
                ["_rag_source_url"] = result.SourceUrl,
            },
        };
}
