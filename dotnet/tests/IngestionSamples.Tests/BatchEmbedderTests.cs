using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class BatchEmbedderTests
{
    [Fact]
    public async Task EmbedAsyncReturnsOneVectorPerText()
    {
        var generator = new FakeEmbeddingGenerator(project: static text => [text.Length, 0, 0]);
        var embedder = new BatchEmbedder(generator, dimensions: 3, maxBatchSize: 64);

        IReadOnlyList<ReadOnlyMemory<float>> results = await embedder.EmbedAsync(["a", "bb", "ccc"]);

        Assert.Equal(3, results.Count);
        Assert.Equal(1f, results[0].Span[0]);
        Assert.Equal(2f, results[1].Span[0]);
        Assert.Equal(3f, results[2].Span[0]);
    }

    [Fact]
    public async Task EmbedAsyncSplitsIntoBoundedBatches()
    {
        var generator = new FakeEmbeddingGenerator(project: static _ => [0, 0, 0]);
        var embedder = new BatchEmbedder(generator, dimensions: 3, maxBatchSize: 2);

        await embedder.EmbedAsync(["a", "b", "c", "d", "e"]);

        Assert.Equal([2, 2, 1], generator.BatchSizes);
    }

    [Fact]
    public async Task EmbedAsyncThrowsWhenDimensionsMismatch()
    {
        var generator = new FakeEmbeddingGenerator(forceReturnedVectorLength: 2);
        var embedder = new BatchEmbedder(generator, dimensions: 3);

        await Assert.ThrowsAsync<IngestionValidationException>(() => embedder.EmbedAsync(["a"]));
    }

    [Fact]
    public async Task EmbedAsyncThrowsWhenValueIsNonFinite()
    {
        var generator = new FakeEmbeddingGenerator(
            project: static _ => [0, 0, 0],
            forceNonFiniteValue: float.NaN);
        var embedder = new BatchEmbedder(generator, dimensions: 3);

        await Assert.ThrowsAsync<IngestionValidationException>(() => embedder.EmbedAsync(["a"]));
    }

    [Fact]
    public async Task EmbedAsyncPropagatesCancellation()
    {
        var generator = new FakeEmbeddingGenerator();
        var embedder = new BatchEmbedder(generator, dimensions: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => embedder.EmbedAsync(["a", "b"], cts.Token));
    }

    [Fact]
    public void ConstructorRejectsNonPositiveDimensions()
    {
        Assert.Throws<IngestionValidationException>(
            () => new BatchEmbedder(new FakeEmbeddingGenerator(), dimensions: 0));
    }

    [Fact]
    public void ConstructorRejectsNonPositiveMaxBatchSize()
    {
        Assert.Throws<IngestionValidationException>(
            () => new BatchEmbedder(new FakeEmbeddingGenerator(), dimensions: 3, maxBatchSize: 0));
    }
}
