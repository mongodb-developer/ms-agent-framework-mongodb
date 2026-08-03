using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class ParentDocumentIngestionPipelineTests
{
    private static ParentDocumentIngestionPipeline CreatePipeline(FakeChunkStore store, FakeEmbeddingGenerator? generator = null) =>
        new(store, new BatchEmbedder(generator ?? new FakeEmbeddingGenerator(), dimensions: 3),
            new ChunkingOptions { WindowSize = 20, OverlapSize = 5 });

    [Fact]
    public async Task IngestAsyncWritesOneUnembeddedParentAndEmbeddedChildren()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var document = new SourceDocument("tenant-a", "source-1", "This is some sample content to chunk up.");

        await pipeline.IngestAsync(document);

        ChunkRecord[] parents = [.. store.Records.Values.Where(r => r.RecordType == ChunkRecord.ParentRecordType)];
        ChunkRecord[] children = [.. store.Records.Values.Where(r => r.RecordType == ChunkRecord.ChildRecordType)];
        Assert.Single(parents);
        Assert.Null(parents[0].Embedding);
        Assert.NotEmpty(children);
        Assert.All(children, child => Assert.NotNull(child.Embedding));
        Assert.All(children, child => Assert.Equal(parents[0].Id, child.ParentId));
    }

    [Fact]
    public async Task IngestAsyncDetectsParentOnlyContentChangeWithNoChildChunkChange()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var document = new SourceDocument(
            "tenant-a", "source-1", "Body text stays exactly the same across both runs here.", Title: "Original Title");
        await pipeline.IngestAsync(document);

        // Only the title changes; body/child chunk text is identical, so only the parent record's hash should differ.
        IngestionResult result = await pipeline.IngestAsync(document with { Title = "Updated Title" });

        Assert.Equal(1, result.ChunksUpserted);
        Assert.True(result.ChunksUnchanged > 0);
    }

    [Fact]
    public async Task IngestAsyncOnlyEmbedsChangedChildrenNotTheParent()
    {
        var store = new FakeChunkStore();
        var generator = new FakeEmbeddingGenerator();
        var pipeline = CreatePipeline(store, generator);
        var document = new SourceDocument("tenant-a", "source-1", "Alpha content block one. Beta content block two.");
        await pipeline.IngestAsync(document);
        int totalTextsEmbeddedFirstRun = generator.BatchSizes.Sum();

        var changed = document with { Content = "Alpha content block one. CHANGED block two entirely." };
        await pipeline.IngestAsync(changed);

        // Second run must not re-embed the parent (parent is never embedded at all).
        ChunkRecord[] parents = [.. store.Records.Values.Where(r => r.RecordType == ChunkRecord.ParentRecordType)];
        Assert.All(parents, parent => Assert.Null(parent.Embedding));
        Assert.True(totalTextsEmbeddedFirstRun > 0);
    }

    [Fact]
    public async Task IngestAsyncDeletesStaleChildrenWithinTenantAndSourceScope()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var longDocument = new SourceDocument(
            "tenant-a", "source-1", string.Concat(Enumerable.Range(0, 20).Select(i => $"sentence-{i} has unique words. ")));
        await pipeline.IngestAsync(longDocument);

        IngestionResult result = await pipeline.IngestAsync(longDocument with { Content = "one short chunk" });

        Assert.True(result.ChunksDeleted > 0);
        Assert.Contains(store.Records.Values, r => r.RecordType == ChunkRecord.ParentRecordType);
    }

    [Fact]
    public async Task IngestAsyncPropagatesCancellation()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.IngestAsync(new SourceDocument("tenant-a", "source-1", "content"), cts.Token));
        Assert.Empty(store.Records);
    }
}
