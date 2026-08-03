using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

/// <summary>An in-memory <see cref="IChunkStore"/> substitute used only by offline pipeline tests.</summary>
internal sealed class FakeChunkStore : IChunkStore
{
    private readonly Dictionary<string, ChunkRecord> _records = [];

    public IReadOnlyDictionary<string, ChunkRecord> Records => _records;

    public int UpsertCallCount { get; private set; }

    public int DeleteCallCount { get; private set; }

    public Task<IReadOnlyDictionary<string, string>> GetExistingHashesAsync(
        string tenantId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string> hashes = _records.Values
            .Where(record => record.TenantId == tenantId && record.SourceId == sourceId)
            .ToDictionary(record => record.Id, record => record.ContentHash, StringComparer.Ordinal);
        return Task.FromResult(hashes);
    }

    public Task UpsertAsync(IReadOnlyList<ChunkRecord> records, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpsertCallCount++;
        foreach (ChunkRecord record in records)
        {
            // Mirrors MongoChunkStore's replace filter, which always matches _id together with tenant_id +
            // source_id + record_type (never _id alone): an _id that collides with a different tenant/source/type
            // must never silently overwrite that other record.
            if (_records.TryGetValue(record.Id, out ChunkRecord? existing) &&
                (existing.TenantId != record.TenantId ||
                 existing.SourceId != record.SourceId ||
                 existing.RecordType != record.RecordType))
            {
                throw new IngestionValidationException(
                    $"Record '{record.Id}' already exists for a different tenant/source/record-type scope.");
            }

            _records[record.Id] = record;
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteAsync(
        string tenantId,
        string sourceId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Count == 0)
        {
            return Task.FromResult(0);
        }

        DeleteCallCount++;
        int deleted = 0;
        foreach (string id in ids)
        {
            if (_records.TryGetValue(id, out ChunkRecord? record) &&
                record.TenantId == tenantId && record.SourceId == sourceId)
            {
                _records.Remove(id);
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }

    public Task<int> DeleteSourceAsync(string tenantId, string sourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] matchingIds = [.. _records.Values
            .Where(record => record.TenantId == tenantId && record.SourceId == sourceId)
            .Select(record => record.Id)];
        foreach (string id in matchingIds)
        {
            _records.Remove(id);
        }

        return Task.FromResult(matchingIds.Length);
    }

    public Task<IReadOnlyList<string>> ListSourceIdsAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> sourceIds = [.. _records.Values
            .Where(record => record.TenantId == tenantId)
            .Select(record => record.SourceId)
            .Distinct(StringComparer.Ordinal)];
        return Task.FromResult(sourceIds);
    }
}
