using MongoDB.Bson;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-local, storage-neutral representation of one chunk or parent record ready to be written by an
/// <see cref="IChunkStore"/>. <see cref="RecordType"/> is <c>"chunk"</c> for flat incremental ingestion or
/// <c>"parent"</c>/<c>"child"</c> for the parent-document pattern (docs/spec/features/rag.md's parent-document
/// schema). <see cref="Embedding"/> is <see langword="null"/> for parent records, which are never embedded or
/// included in Vector Search.
/// </summary>
public sealed record ChunkRecord(
    string Id,
    string TenantId,
    string SourceId,
    string? ParentId,
    string RecordType,
    string Text,
    string ContentHash,
    ReadOnlyMemory<float>? Embedding,
    string? SourceName,
    string? SourceUrl)
{
    /// <summary>Gets the reserved field name every store implementation writes <see cref="Id"/> under.</summary>
    public const string IdFieldName = "_id";

    /// <summary>Gets the reserved field name every store implementation writes <see cref="TenantId"/> under.</summary>
    public const string TenantIdFieldName = "tenant_id";

    /// <summary>Gets the reserved field name every store implementation writes <see cref="SourceId"/> under.</summary>
    public const string SourceIdFieldName = "source_id";

    /// <summary>Gets the reserved field name every store implementation writes <see cref="ParentId"/> under.</summary>
    public const string ParentIdFieldName = "parent_id";

    /// <summary>Gets the reserved field name every store implementation writes <see cref="RecordType"/> under.</summary>
    public const string RecordTypeFieldName = "record_type";

    /// <summary>Gets the reserved field name every store implementation writes <see cref="Text"/> under.</summary>
    public const string TextFieldName = "text";

    /// <summary>Gets the reserved field name every store implementation writes <see cref="ContentHash"/> under.</summary>
    public const string ContentHashFieldName = "content_hash";

    /// <summary>Gets the reserved field name every store implementation writes the embedding vector under.</summary>
    public const string EmbeddingFieldName = "embedding";

    /// <summary>Gets the parent record type discriminator value.</summary>
    public const string ParentRecordType = "parent";

    /// <summary>Gets the child record type discriminator value.</summary>
    public const string ChildRecordType = "child";

    /// <summary>Gets the flat (non-parent-document) chunk record type discriminator value.</summary>
    public const string FlatChunkRecordType = "chunk";

    /// <summary>Converts this record into the MongoDB document shape every <see cref="IChunkStore"/> writes.</summary>
    public BsonDocument ToBsonDocument()
    {
        var document = new BsonDocument
        {
            { IdFieldName, Id },
            { TenantIdFieldName, TenantId },
            { SourceIdFieldName, SourceId },
            { RecordTypeFieldName, RecordType },
            { TextFieldName, Text },
            { ContentHashFieldName, ContentHash },
        };

        if (ParentId is not null)
        {
            document[ParentIdFieldName] = ParentId;
        }

        if (Embedding is { } embedding)
        {
            var array = new BsonArray(embedding.Length);
            foreach (float value in embedding.Span)
            {
                array.Add(new BsonDouble(value));
            }

            document[EmbeddingFieldName] = array;
        }

        if (SourceName is not null || SourceUrl is not null)
        {
            var source = new BsonDocument();
            if (SourceName is not null)
            {
                source["name"] = SourceName;
            }

            if (SourceUrl is not null)
            {
                source["url"] = SourceUrl;
            }

            document["source"] = source;
        }

        return document;
    }
}
