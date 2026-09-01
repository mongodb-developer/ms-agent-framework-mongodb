using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework.Tests.Internal;

public sealed class EmbeddingValidatorTests
{
    [Fact]
    public void Normalize_returns_valid_vectors()
    {
        ReadOnlyMemory<float>[] vectors =
            [new float[] { 1.0f, 2.0f }, new float[] { 3.0f, 4.0f }];

        IReadOnlyList<ReadOnlyMemory<float>> result =
            EmbeddingValidator.Normalize(vectors, expectedCount: 2, dimensions: 2);

        Assert.Equal(2, result.Count);
        Assert.Equal([1.0f, 2.0f], result[0].ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_dimensions_rejects_non_positive_values(int dimensions)
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => EmbeddingValidator.ValidateDimensions(dimensions));
    }

    [Fact]
    public void Normalize_rejects_wrong_count()
    {
        ReadOnlyMemory<float>[] vectors = [new float[] { 1.0f, 2.0f }];

        Assert.Throws<MongoDBEmbeddingException>(
            () => EmbeddingValidator.Normalize(vectors, expectedCount: 2, dimensions: 2));
    }

    [Fact]
    public void Normalize_rejects_wrong_dimensions()
    {
        ReadOnlyMemory<float>[] vectors = [new float[] { 1.0f }];

        Assert.Throws<MongoDBEmbeddingException>(
            () => EmbeddingValidator.Normalize(vectors, expectedCount: 1, dimensions: 2));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Normalize_rejects_non_finite_values(float value)
    {
        ReadOnlyMemory<float>[] vectors = [new float[] { value }];

        Assert.Throws<MongoDBEmbeddingException>(
            () => EmbeddingValidator.Normalize(vectors, expectedCount: 1, dimensions: 1));
    }
}
