using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// The production <see cref="IParentLookup"/> implementation: a single bounded query against the same collection
/// child chunks are stored in (docs/spec/features/rag.md's "same-database ... or same-collection lookup"
/// requirement), constrained to <c>record_type == "parent"</c> and the caller's tenant, and capped at the supplied
/// parent ID count so parent hydration can never fan out beyond what the caller's own bounded ID list already
/// allows.
/// </summary>
public sealed class MongoParentLookup : IParentLookup
{
    private readonly IMongoCollection<BsonDocument> _collection;

    /// <summary>Initializes a lookup over an injected, caller-owned collection.</summary>
    public MongoParentLookup(IMongoCollection<BsonDocument> collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParentDocument>> FindParentsAsync(
        IReadOnlyList<string> parentIds,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentIds);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new IngestionValidationException($"{nameof(tenantId)} must not be empty.");
        }

        if (parentIds.Count == 0)
        {
            return [];
        }

        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.In(ChunkRecord.IdFieldName, parentIds),
            Builders<BsonDocument>.Filter.Eq(ChunkRecord.RecordTypeFieldName, ChunkRecord.ParentRecordType),
            Builders<BsonDocument>.Filter.Eq(ChunkRecord.TenantIdFieldName, tenantId));

        List<BsonDocument> documents = await _collection
            .Find(filter)
            .Limit(parentIds.Count)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var results = new List<ParentDocument>(documents.Count);
        foreach (BsonDocument document in documents)
        {
            BsonDocument? source = document.TryGetValue("source", out BsonValue sourceValue) && sourceValue is BsonDocument sourceDocument
                ? sourceDocument
                : null;
            results.Add(new ParentDocument(
                ParentId: document[ChunkRecord.IdFieldName].AsString,
                Content: document[ChunkRecord.TextFieldName].AsString,
                SourceName: source is not null && source.TryGetValue("name", out BsonValue name) ? name.AsString : null,
                SourceUrl: source is not null && source.TryGetValue("url", out BsonValue url) ? url.AsString : null));
        }

        return results;
    }
}
