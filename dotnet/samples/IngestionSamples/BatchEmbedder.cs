using Microsoft.Extensions.AI;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// Batches embedding generation for ingestion, calling the same public <see cref="IEmbeddingGenerator{TInput,
/// TEmbedding}"/> abstraction the runtime <c>MongoDBRAGProvider</c> and <c>MongoDBMemoryProvider</c> use
/// (docs/spec/features/ingestion.md's "call the same embedding abstraction" requirement), in bounded batches, with
/// per-vector dimension and finite-value validation and full cancellation propagation.
/// </summary>
public sealed class BatchEmbedder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly int _dimensions;
    private readonly int _maxBatchSize;

    /// <summary>Initializes a batch embedder over an injected, caller-owned embedding generator.</summary>
    /// <param name="generator">The embedding generator to call, in batches, for changed/new chunk text.</param>
    /// <param name="dimensions">The expected embedding vector length. Must be positive.</param>
    /// <param name="maxBatchSize">The maximum number of texts sent to one <c>GenerateAsync</c> call.</param>
    public BatchEmbedder(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        int dimensions,
        int maxBatchSize = 64)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        if (dimensions <= 0)
        {
            throw new IngestionValidationException($"{nameof(dimensions)} must be positive.");
        }

        if (maxBatchSize <= 0)
        {
            throw new IngestionValidationException($"{nameof(maxBatchSize)} must be positive.");
        }

        _dimensions = dimensions;
        _maxBatchSize = maxBatchSize;
    }

    /// <summary>
    /// Embeds every text in bounded batches of at most the configured maximum batch size, validating each returned
    /// vector's dimensions and finiteness before returning, in the same order as <paramref name="texts"/>.
    /// </summary>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new List<ReadOnlyMemory<float>>(texts.Count);
        for (int offset = 0; offset < texts.Count; offset += _maxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] batch = [.. texts.Skip(offset).Take(_maxBatchSize)];
            GeneratedEmbeddings<Embedding<float>> generated = await _generator
                .GenerateAsync(batch, options: null, cancellationToken)
                .ConfigureAwait(false);
            if (generated.Count != batch.Length)
            {
                throw new IngestionValidationException(
                    $"Embedding generator returned {generated.Count} vectors; expected {batch.Length}.");
            }

            for (int index = 0; index < generated.Count; index++)
            {
                ReadOnlyMemory<float> vector = generated[index].Vector;
                if (vector.Length != _dimensions)
                {
                    throw new IngestionValidationException(
                        $"Embedding {offset + index} has {vector.Length} dimensions; expected {_dimensions}.");
                }

                foreach (float value in vector.Span)
                {
                    if (!float.IsFinite(value))
                    {
                        throw new IngestionValidationException(
                            $"Embedding {offset + index} contains a non-finite value.");
                    }
                }

                results.Add(vector);
            }
        }

        return results;
    }
}
