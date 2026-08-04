using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

/// <summary>
/// Verifies the scope-safety guard <see cref="FakeChunkStore"/> mirrors from <see cref="MongoChunkStore"/>'s
/// replace filter: an <c>_id</c> collision must never silently cross a tenant/source/record-type scope.
/// </summary>
public sealed class FakeChunkStoreTests
{
    [Fact]
    public async Task UpsertAsyncRejectsAnIdCollisionAcrossDifferentTenants()
    {
        var store = new FakeChunkStore();
        var original = new ChunkRecord(
            "shared-id", "tenant-a", "source-1", ParentId: null, ChunkRecord.FlatChunkRecordType,
            "text", "hash-a", Embedding: null, SourceName: null, SourceUrl: null);
        await store.UpsertAsync([original]);

        var colliding = original with { TenantId = "tenant-b" };

        await Assert.ThrowsAsync<IngestionValidationException>(() => store.UpsertAsync([colliding]));
        Assert.Equal("tenant-a", store.Records["shared-id"].TenantId);
    }

    [Fact]
    public async Task UpsertAsyncRejectsAnIdCollisionAcrossDifferentSourcesWithinTheSameTenant()
    {
        var store = new FakeChunkStore();
        var original = new ChunkRecord(
            "shared-id", "tenant-a", "source-1", ParentId: null, ChunkRecord.FlatChunkRecordType,
            "text", "hash-a", Embedding: null, SourceName: null, SourceUrl: null);
        await store.UpsertAsync([original]);

        var colliding = original with { SourceId = "source-2" };

        await Assert.ThrowsAsync<IngestionValidationException>(() => store.UpsertAsync([colliding]));
        Assert.Equal("source-1", store.Records["shared-id"].SourceId);
    }

    [Fact]
    public async Task UpsertAsyncRejectsAnIdCollisionAcrossDifferentRecordTypes()
    {
        var store = new FakeChunkStore();
        var original = new ChunkRecord(
            "shared-id", "tenant-a", "source-1", ParentId: null, ChunkRecord.ParentRecordType,
            "text", "hash-a", Embedding: null, SourceName: null, SourceUrl: null);
        await store.UpsertAsync([original]);

        var colliding = original with { RecordType = ChunkRecord.FlatChunkRecordType };

        await Assert.ThrowsAsync<IngestionValidationException>(() => store.UpsertAsync([colliding]));
    }

    [Fact]
    public async Task UpsertAsyncAllowsReplacingAnExistingRecordWithinTheSameScope()
    {
        var store = new FakeChunkStore();
        var original = new ChunkRecord(
            "shared-id", "tenant-a", "source-1", ParentId: null, ChunkRecord.FlatChunkRecordType,
            "text", "hash-a", Embedding: null, SourceName: null, SourceUrl: null);
        await store.UpsertAsync([original]);

        var updated = original with { ContentHash = "hash-b" };
        await store.UpsertAsync([updated]);

        Assert.Equal("hash-b", store.Records["shared-id"].ContentHash);
    }

    [Fact]
    public async Task DeleteSourceAsyncRemovesOnlyRecordsForTheGivenTenantAndSource()
    {
        var store = new FakeChunkStore();
        await store.UpsertAsync(
        [
            Record("chunk-1", "tenant-a", "source-1"),
            Record("chunk-2", "tenant-a", "source-1"),
            Record("chunk-3", "tenant-a", "source-2"),
            Record("chunk-4", "tenant-b", "source-1"),
        ]);

        int deleted = await store.DeleteSourceAsync("tenant-a", "source-1");

        Assert.Equal(2, deleted);
        Assert.DoesNotContain(store.Records.Values, r => r.TenantId == "tenant-a" && r.SourceId == "source-1");
        Assert.Contains(store.Records.Values, r => r.TenantId == "tenant-a" && r.SourceId == "source-2");
        Assert.Contains(store.Records.Values, r => r.TenantId == "tenant-b" && r.SourceId == "source-1");
    }

    [Fact]
    public async Task DeleteSourceAsyncReturnsZeroForAnUnknownSource()
    {
        var store = new FakeChunkStore();

        int deleted = await store.DeleteSourceAsync("tenant-a", "source-does-not-exist");

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task DeleteSourceAsyncPropagatesCancellation()
    {
        var store = new FakeChunkStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.DeleteSourceAsync("tenant-a", "source-1", cts.Token));
    }

    [Fact]
    public async Task ListSourceIdsAsyncReturnsDistinctSourceIdsForTheGivenTenantOnly()
    {
        var store = new FakeChunkStore();
        await store.UpsertAsync(
        [
            Record("chunk-1", "tenant-a", "source-1"),
            Record("chunk-2", "tenant-a", "source-1"),
            Record("chunk-3", "tenant-a", "source-2"),
            Record("chunk-4", "tenant-b", "source-3"),
        ]);

        IReadOnlyList<string> sourceIds = await store.ListSourceIdsAsync("tenant-a");

        Assert.Equal(["source-1", "source-2"], sourceIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListSourceIdsAsyncPropagatesCancellation()
    {
        var store = new FakeChunkStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ListSourceIdsAsync("tenant-a", cts.Token));
    }

    private static ChunkRecord Record(string id, string tenantId, string sourceId) =>
        new(id, tenantId, sourceId, ParentId: null, ChunkRecord.FlatChunkRecordType, "text", "hash",
            Embedding: null, SourceName: null, SourceUrl: null);
}
