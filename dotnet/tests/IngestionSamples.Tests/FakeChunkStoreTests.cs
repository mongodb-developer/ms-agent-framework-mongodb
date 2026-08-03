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
}
