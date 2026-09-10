using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class IncrementalIngestionPipelineTests
{
    private static IncrementalIngestionPipeline CreatePipeline(FakeChunkStore store, FakeEmbeddingGenerator? generator = null) =>
        new(store, new BatchEmbedder(generator ?? new FakeEmbeddingGenerator(), dimensions: 3),
            new ChunkingOptions { WindowSize = 20, OverlapSize = 5 });

    [Fact]
    public async Task IngestAsyncWritesEveryNewChunkOnFirstRun()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var document = new SourceDocument("tenant-a", "source-1", "This is some sample content to chunk up.");

        IngestionResult result = await pipeline.IngestAsync(document);

        Assert.True(result.ChunksUpserted > 0);
        Assert.Equal(0, result.ChunksUnchanged);
        Assert.Equal(0, result.ChunksDeleted);
        Assert.Equal(result.ChunksUpserted, store.Records.Count);
    }

    [Fact]
    public async Task IngestAsyncSkipsUnchangedChunksOnRerun()
    {
        var store = new FakeChunkStore();
        var generator = new FakeEmbeddingGenerator();
        var pipeline = CreatePipeline(store, generator);
        var document = new SourceDocument("tenant-a", "source-1", "This is some sample content to chunk up.");

        IngestionResult first = await pipeline.IngestAsync(document);
        int embedCallsAfterFirst = generator.BatchSizes.Count;

        IngestionResult second = await pipeline.IngestAsync(document);

        Assert.Equal(0, second.ChunksUpserted);
        Assert.Equal(first.ChunksUpserted, second.ChunksUnchanged);
        Assert.Equal(0, second.ChunksDeleted);
        // No new embedding calls should happen for a rerun over unchanged content.
        Assert.Equal(embedCallsAfterFirst, generator.BatchSizes.Count);
    }

    [Fact]
    public async Task IngestAsyncOnlyEmbedsAndUpsertsChangedChunks()
    {
        var store = new FakeChunkStore();
        var generator = new FakeEmbeddingGenerator();
        var pipeline = CreatePipeline(store, generator);
        var original = new SourceDocument("tenant-a", "source-1", "Alpha content block one. Beta content block two.");
        await pipeline.IngestAsync(original);

        var changed = original with { Content = "Alpha content block one. CHANGED block two entirely." };
        IngestionResult result = await pipeline.IngestAsync(changed);

        Assert.True(result.ChunksUpserted > 0);
        Assert.True(result.ChunksUnchanged > 0);
    }

    [Fact]
    public async Task IngestAsyncDeletesStaleChunksNoLongerProduced()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var longDocument = new SourceDocument(
            "tenant-a",
            "source-1",
            string.Concat(Enumerable.Range(0, 20).Select(i => $"sentence-{i} has unique words. ")));
        await pipeline.IngestAsync(longDocument);
        int originalChunkCount = store.Records.Count;
        Assert.True(originalChunkCount > 1);

        var shortDocument = longDocument with { Content = "one short chunk" };
        IngestionResult result = await pipeline.IngestAsync(shortDocument);

        Assert.True(result.ChunksDeleted > 0);
        // The one surviving chunk index (0) is updated in place, not added anew, so the final count is simply the
        // original minus what got deleted.
        Assert.Equal(originalChunkCount - result.ChunksDeleted, store.Records.Count);
    }

    [Fact]
    public async Task IngestAsyncUpsertsChunksWhenOnlyTitleChangesWithIdenticalContent()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var original = new SourceDocument("tenant-a", "source-1", "Identical content that never changes.", Title: "Original Title");
        await pipeline.IngestAsync(original);

        var titleOnlyChange = original with { Title = "Updated Title" };
        IngestionResult result = await pipeline.IngestAsync(titleOnlyChange);

        // An attribution-only (title) edit with identical chunk text must still be detected as a change, so the
        // stored record's title metadata is corrected rather than left stale forever.
        Assert.True(result.ChunksUpserted > 0);
        Assert.Equal(0, result.ChunksUnchanged);
        Assert.All(store.Records.Values, record => Assert.Equal("Updated Title", record.SourceName));
    }

    [Fact]
    public async Task IngestAsyncUpsertsChunksWhenOnlyUrlChangesWithIdenticalContent()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var original = new SourceDocument(
            "tenant-a", "source-1", "Identical content that never changes.", Url: "https://example.test/original");
        await pipeline.IngestAsync(original);

        var urlOnlyChange = original with { Url = "https://example.test/updated" };
        IngestionResult result = await pipeline.IngestAsync(urlOnlyChange);

        // An attribution-only (URL) edit with identical chunk text must still be detected as a change, so the
        // stored record's URL metadata is corrected rather than left stale forever.
        Assert.True(result.ChunksUpserted > 0);
        Assert.Equal(0, result.ChunksUnchanged);
        Assert.All(store.Records.Values, record => Assert.Equal("https://example.test/updated", record.SourceUrl));
    }

    [Fact]
    public async Task IngestAsyncSkipsRerunWhenTitleAndUrlAndContentAreAllUnchanged()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var document = new SourceDocument(
            "tenant-a", "source-1", "Identical content that never changes.", Title: "Title", Url: "https://example.test");
        await pipeline.IngestAsync(document);

        IngestionResult result = await pipeline.IngestAsync(document);

        Assert.Equal(0, result.ChunksUpserted);
        Assert.True(result.ChunksUnchanged > 0);
    }

    [Fact]
    public async Task IngestAsyncOnlyDeletesWithinTheSameTenantAndSourceScope()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        var tenantADocument = new SourceDocument("tenant-a", "source-1", "Tenant A original content here.");
        var tenantBDocument = new SourceDocument("tenant-b", "source-1", "Tenant B original content here.");
        await pipeline.IngestAsync(tenantADocument);
        await pipeline.IngestAsync(tenantBDocument);

        await pipeline.IngestAsync(tenantADocument with { Content = "Completely different tenant A content now." });

        // Tenant B's chunks for the same source ID must be untouched by tenant A's re-ingestion.
        Assert.Contains(store.Records.Values, record => record.TenantId == "tenant-b");
    }

    [Fact]
    public async Task IngestAsyncPropagatesCancellationBeforeAnyStoreCall()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.IngestAsync(new SourceDocument("tenant-a", "source-1", "content"), cts.Token));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task IngestAsyncRejectsAnInvalidDocument()
    {
        var store = new FakeChunkStore();
        var pipeline = CreatePipeline(store);

        await Assert.ThrowsAsync<IngestionValidationException>(
            () => pipeline.IngestAsync(new SourceDocument("", "source-1", "content")));
    }
}
