using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class SourceManifestReconcilerTests
{
    private static ChunkRecord Record(string id, string tenantId, string sourceId) =>
        new(id, tenantId, sourceId, ParentId: null, ChunkRecord.FlatChunkRecordType, "text", "hash",
            Embedding: null, SourceName: null, SourceUrl: null);

    [Fact]
    public async Task ReconcileAsyncTombstonesOnlySourcesAbsentFromTheCurrentManifest()
    {
        var store = new FakeChunkStore();
        await store.UpsertAsync(
        [
            Record("chunk-1", "tenant-a", "source-1"),
            Record("chunk-2", "tenant-a", "source-2"),
            Record("chunk-3", "tenant-a", "source-3"),
        ]);
        var reconciler = new SourceManifestReconciler(store);

        SourceReconciliationResult result = await reconciler.ReconcileAsync("tenant-a", ["source-1", "source-3"]);

        Assert.Equal(["source-2"], result.DisappearedSourceIds);
        Assert.Equal(1, result.RecordsDeleted);
        Assert.Contains(store.Records.Values, r => r.SourceId == "source-1");
        Assert.Contains(store.Records.Values, r => r.SourceId == "source-3");
        Assert.DoesNotContain(store.Records.Values, r => r.SourceId == "source-2");
    }

    [Fact]
    public async Task ReconcileAsyncNeverTouchesAnotherTenantsSameNamedSource()
    {
        var store = new FakeChunkStore();
        await store.UpsertAsync(
        [
            Record("chunk-1", "tenant-a", "source-1"),
            Record("chunk-2", "tenant-b", "source-1"),
        ]);
        var reconciler = new SourceManifestReconciler(store);

        SourceReconciliationResult result = await reconciler.ReconcileAsync("tenant-a", []);

        Assert.Equal(["source-1"], result.DisappearedSourceIds);
        Assert.Equal(1, result.RecordsDeleted);
        Assert.Contains(store.Records.Values, r => r.TenantId == "tenant-b" && r.SourceId == "source-1");
    }

    [Fact]
    public async Task ReconcileAsyncDeletesNothingWhenEveryStoredSourceIsStillCurrent()
    {
        var store = new FakeChunkStore();
        await store.UpsertAsync([Record("chunk-1", "tenant-a", "source-1")]);
        var reconciler = new SourceManifestReconciler(store);

        SourceReconciliationResult result = await reconciler.ReconcileAsync("tenant-a", ["source-1"]);

        Assert.Empty(result.DisappearedSourceIds);
        Assert.Equal(0, result.RecordsDeleted);
        Assert.Single(store.Records);
    }

    [Fact]
    public async Task ReconcileAsyncPropagatesCancellationBeforeDeletingAnything()
    {
        var store = new FakeChunkStore();
        await store.UpsertAsync([Record("chunk-1", "tenant-a", "source-1")]);
        var reconciler = new SourceManifestReconciler(store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => reconciler.ReconcileAsync("tenant-a", [], cts.Token));
        Assert.Single(store.Records);
    }

    [Fact]
    public void ConstructorRejectsNullChunkStore()
    {
        Assert.Throws<ArgumentNullException>(() => new SourceManifestReconciler(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReconcileAsyncRejectsEmptyTenantId(string? tenantId)
    {
        var reconciler = new SourceManifestReconciler(new FakeChunkStore());

        await Assert.ThrowsAsync<IngestionValidationException>(
            () => reconciler.ReconcileAsync(tenantId!, []));
    }

    [Fact]
    public async Task ReconcileAsyncRejectsNullCurrentSourceIds()
    {
        var reconciler = new SourceManifestReconciler(new FakeChunkStore());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => reconciler.ReconcileAsync("tenant-a", null!));
    }
}
