using Microsoft.Extensions.AI;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

/// <summary>A deterministic, in-memory embedding generator used only by offline tests.</summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Func<string, float[]> _project;
    private readonly int? _forceReturnedVectorLength;
    private readonly float? _forceNonFiniteValue;
    private readonly List<int> _batchSizes = [];

    public FakeEmbeddingGenerator(
        Func<string, float[]>? project = null,
        int? forceReturnedVectorLength = null,
        float? forceNonFiniteValue = null)
    {
        _project = project ?? (static text => [text.Length, 0, 0]);
        _forceReturnedVectorLength = forceReturnedVectorLength;
        _forceNonFiniteValue = forceNonFiniteValue;
    }

    public IReadOnlyList<int> BatchSizes => _batchSizes;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] materialized = [.. values];
        _batchSizes.Add(materialized.Length);

        var generated = new GeneratedEmbeddings<Embedding<float>>();
        foreach (string value in materialized)
        {
            float[] vector = _project(value);
            if (_forceReturnedVectorLength is { } length)
            {
                vector = new float[length];
            }

            if (_forceNonFiniteValue is { } nonFinite && vector.Length > 0)
            {
                vector[0] = nonFinite;
            }

            generated.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(generated);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
