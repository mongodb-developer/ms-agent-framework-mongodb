namespace MongoDB.AgentFramework.Internal;

internal static class EmbeddingValidator
{
    public static int ValidateDimensions(int dimensions)
    {
        if (dimensions <= 0)
        {
            throw new MongoDBConfigurationException(
                "Embedding dimensions must be a positive integer.");
        }

        return dimensions;
    }

    public static IReadOnlyList<ReadOnlyMemory<float>> Normalize(
        IEnumerable<ReadOnlyMemory<float>> embeddings,
        int expectedCount,
        int dimensions)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ValidateDimensions(dimensions);

        if (expectedCount < 0)
        {
            throw new MongoDBConfigurationException(
                "Expected embedding count must not be negative.");
        }

        ReadOnlyMemory<float>[] vectors = embeddings.ToArray();
        if (vectors.Length != expectedCount)
        {
            throw new MongoDBEmbeddingException(
                $"Embedding generator returned {vectors.Length} vectors; expected {expectedCount}.");
        }

        for (int vectorIndex = 0; vectorIndex < vectors.Length; vectorIndex++)
        {
            ReadOnlySpan<float> vector = vectors[vectorIndex].Span;
            if (vector.Length != dimensions)
            {
                throw new MongoDBEmbeddingException(
                    $"Embedding {vectorIndex} has {vector.Length} dimensions; expected {dimensions}.");
            }

            for (int valueIndex = 0; valueIndex < vector.Length; valueIndex++)
            {
                if (!float.IsFinite(vector[valueIndex]))
                {
                    throw new MongoDBEmbeddingException(
                        $"Embedding {vectorIndex} value {valueIndex} must be finite.");
                }
            }
        }

        return vectors;
    }
}
