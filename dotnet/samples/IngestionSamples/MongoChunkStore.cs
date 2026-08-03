using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// The production-shaped <see cref="IChunkStore"/> implementation used by the console samples and the
/// credential-gated integration tests. Reads are bounded/streamed through the driver's cursor batching rather than
/// materializing the whole collection, and writes/deletes are issued in bounded sub-batches so one ingestion call
/// never issues one unbounded bulk operation. The injected collection remains caller-owned; this store never
/// creates it, and it never touches documents outside the tenant+source scope passed to each call.
/// </summary>
public sealed class MongoChunkStore : IChunkStore
{
    /// <summary>The maximum number of records written or deleted per underlying MongoDB round trip.</summary>
    public const int MaxBatchSize = 500;

    /// <summary>The cursor batch size used while streaming existing content hashes.</summary>
    public const int ReadBatchSize = 500;

    private readonly IMongoCollection<BsonDocument> _collection;

    /// <summary>Initializes a store over an injected, caller-owned collection.</summary>
    public MongoChunkStore(IMongoCollection<BsonDocument> collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetExistingHashesAsync(
        string tenantId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        RequireText(tenantId, nameof(tenantId));
        RequireText(sourceId, nameof(sourceId));

        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq(ChunkRecord.TenantIdFieldName, tenantId),
            Builders<BsonDocument>.Filter.Eq(ChunkRecord.SourceIdFieldName, sourceId));
        ProjectionDefinition<BsonDocument> projection = Builders<BsonDocument>.Projection
            .Include(ChunkRecord.IdFieldName)
            .Include(ChunkRecord.ContentHashFieldName);

        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        using IAsyncCursor<BsonDocument> cursor = await _collection
            .Find(filter, new FindOptions { BatchSize = ReadBatchSize })
            .Project(projection)
            .ToCursorAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (BsonDocument document in cursor.Current)
            {
                results[document[ChunkRecord.IdFieldName].AsString] =
                    document[ChunkRecord.ContentHashFieldName].AsString;
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        IReadOnlyList<ChunkRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        for (int offset = 0; offset < records.Count; offset += MaxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<WriteModel<BsonDocument>>();
            foreach (ChunkRecord record in records.Skip(offset).Take(MaxBatchSize))
            {
                BsonDocument document = record.ToBsonDocument();
                batch.Add(new ReplaceOneModel<BsonDocument>(
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.IdFieldName, record.Id),
                    document)
                {
                    IsUpsert = true,
                });
            }

            await _collection.BulkWriteAsync(batch, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(
        string tenantId,
        string sourceId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        RequireText(tenantId, nameof(tenantId));
        RequireText(sourceId, nameof(sourceId));
        ArgumentNullException.ThrowIfNull(ids);

        // An empty ID list is a deliberate no-op rather than a filter with only tenant/source scope: this
        // guarantees DeleteAsync can never be turned into an unbounded per-source delete by an empty stale-ID list.
        if (ids.Count == 0)
        {
            return 0;
        }

        long deleted = 0;
        for (int offset = 0; offset < ids.Count; offset += MaxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] batchIds = [.. ids.Skip(offset).Take(MaxBatchSize)];
            FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq(ChunkRecord.TenantIdFieldName, tenantId),
                Builders<BsonDocument>.Filter.Eq(ChunkRecord.SourceIdFieldName, sourceId),
                Builders<BsonDocument>.Filter.In(ChunkRecord.IdFieldName, batchIds));
            DeleteResult result = await _collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
            deleted += result.DeletedCount;
        }

        return checked((int)deleted);
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IngestionValidationException($"{name} must not be empty.");
        }
    }
}
