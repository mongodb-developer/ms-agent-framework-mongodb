using MongoDB.Bson;

namespace MongoDB.AgentFramework.Internal.IndexManagement;

/// <summary>
/// Pure, non-throwing semantic comparison between an inspected Vector Search index definition and an expected
/// <see cref="MongoDBVectorSearchIndexDefinition"/>. Shared by <see cref="MongoDBMemoryProvider"/>'s existing
/// validate/ensure methods, <see cref="MongoDBRAGProvider"/>'s Vector Search and Hybrid vector-branch validation,
/// and <see cref="MongoDBMemoryIndexManager"/>/<see cref="MongoDBRAGIndexManager"/>'s explicit facades, so this
/// comparison is implemented exactly once (docs/spec/features/index-management.md's shared internal index manager
/// requirement). Comparison is order-insensitive over the <c>fields</c> array and tolerates unrelated extra
/// fields/keys the server may add.
/// </summary>
internal static class VectorSearchIndexEquivalence
{
    /// <summary>
    /// Compares <paramref name="definition"/> (an inspected index's <c>latestDefinition</c>/<c>definition</c>
    /// document) against <paramref name="expected"/>. <paramref name="expected"/>'s <see cref="MongoDBVectorSearchIndexDefinition.Similarity"/>
    /// being <see langword="null"/> skips the similarity comparison entirely, matching callers (Hybrid's vector
    /// branch) that intentionally do not require a specific similarity metric.
    /// </summary>
    public static MongoDBIndexComparison Compare(BsonDocument definition, MongoDBVectorSearchIndexDefinition expected)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(expected);

        BsonDocument[] fields = [.. definition.GetValue("fields", new BsonArray())
            .AsBsonArray.Where(static value => value.IsBsonDocument)
            .Select(static value => value.AsBsonDocument)];
        BsonDocument? vectorField = fields.FirstOrDefault(
            field => field.GetValue("type", "") == "vector" &&
                     field.GetValue("path", "").AsString == expected.VectorFieldName);

        var mismatches = new List<string>();
        if (vectorField is null)
        {
            mismatches.Add(
                $"Vector Search index '{expected.IndexName}' does not map configured field " +
                $"'{expected.VectorFieldName}' as type 'vector'.");
        }
        else
        {
            int dimensions = vectorField.GetValue("numDimensions", 0).ToInt32();
            if (dimensions != expected.VectorDimensions)
            {
                mismatches.Add(
                    $"Vector Search index '{expected.IndexName}' field '{expected.VectorFieldName}' has " +
                    $"{dimensions} dimensions; expected {expected.VectorDimensions}.");
            }

            if (expected.Similarity is not null &&
                vectorField.GetValue("similarity", "") != expected.Similarity)
            {
                mismatches.Add(
                    $"Vector Search index '{expected.IndexName}' field '{expected.VectorFieldName}' has " +
                    $"similarity '{vectorField.GetValue("similarity", "").AsString}'; expected " +
                    $"'{expected.Similarity}'.");
            }
        }

        string[] declaredFilterPaths = [.. fields
            .Where(static field => field.GetValue("type", "") == "filter")
            .Select(static field => field.GetValue("path", "").AsString)];
        foreach (string required in expected.FilterFieldPaths)
        {
            if (!declaredFilterPaths.Contains(required, StringComparer.Ordinal))
            {
                mismatches.Add(
                    $"Vector Search index '{expected.IndexName}' does not map required filter field " +
                    $"'{required}' as type 'filter'.");
            }
        }

        // Extra declared filter fields beyond what is required, or extra top-level definition keys, are
        // compatible differences: they do not prevent the mandatory/scope filter fields this definition requires
        // from working, so they are recorded for visibility rather than treated as actionable.
        string[] extraFilterPaths = [.. declaredFilterPaths
            .Except(expected.FilterFieldPaths, StringComparer.Ordinal)];
        List<string>? compatibleDifferences = extraFilterPaths.Length == 0
            ? null
            : [.. extraFilterPaths.Select(
                path => $"Vector Search index '{expected.IndexName}' declares an additional filter field " +
                    $"'{path}' not required by this definition.")];

        return new MongoDBIndexComparison(mismatches, compatibleDifferences);
    }

    /// <summary>
    /// Checks whether <paramref name="index"/>'s reported <c>type</c> is <c>"vectorSearch"</c>, returning the
    /// actual type when it is not (for a mismatch message) or <see langword="null"/> when it matches.
    /// </summary>
    public static string? CheckIndexType(BsonDocument index)
    {
        string type = index.GetValue("type", "").AsString;
        return string.Equals(type, "vectorSearch", StringComparison.OrdinalIgnoreCase) ? null : type;
    }

    /// <summary>
    /// Checks index type, compares an already-found <paramref name="index"/> against <paramref name="expected"/>,
    /// and (when <paramref name="requireReady"/>) requires <c>READY</c>/queryable status -- throwing
    /// <see cref="MongoDBIndexMismatchException"/>/<see cref="MongoDBIndexNotReadyException"/> on failure. Shared
    /// by <see cref="MongoDBMemoryProvider"/>, <see cref="MongoDBRAGProvider"/>, <see cref="MongoDBMemoryIndexManager"/>,
    /// and <see cref="MongoDBRAGIndexManager"/> so this throw-shape is implemented exactly once.
    /// </summary>
    public static MongoDBIndexComparison Validate(
        BsonDocument index, MongoDBVectorSearchIndexDefinition expected, bool requireReady)
    {
        if (CheckIndexType(index) is { } actualType)
        {
            throw new MongoDBIndexMismatchException(
                $"Vector Search index '{expected.IndexName}' is not a Vector Search index (found type " +
                $"'{actualType}').");
        }

        // A terminal build failure is checked before comparing definitions (and regardless of requireReady): a
        // failed index never becomes ready on its own, so this is always an actionable, non-transient problem --
        // never something bounded polling should retry until its deadline (see MongoDBIndexFailedException).
        if (MongoDBSearchIndexes.Classify(index) == MongoDBIndexStatus.Failed)
        {
            throw new MongoDBIndexFailedException(
                $"Vector Search index '{expected.IndexName}' build failed and requires explicit repair (update " +
                "or recreate); it will never become ready on its own.");
        }

        MongoDBIndexComparison comparison = Compare(MongoDBSearchIndexes.GetDefinition(index), expected);
        if (!comparison.IsCompatible)
        {
            throw new MongoDBIndexMismatchException(
                $"Vector Search index '{expected.IndexName}' does not match the required definition: " +
                string.Join("; ", comparison.Mismatches));
        }

        if (requireReady && MongoDBSearchIndexes.Classify(index) is not MongoDBIndexStatus.Ready)
        {
            throw new MongoDBIndexNotReadyException($"Vector Search index '{expected.IndexName}' is not queryable.");
        }

        return comparison;
    }

    /// <summary>
    /// Builds the Vector Search index definition document (the <c>fields</c> array only; the caller wraps this in
    /// a <c>CreateSearchIndexModel</c>/passes it to <c>UpdateAsync</c>) for <paramref name="definition"/>. Used by
    /// both create and update so the field shape is derived from <see cref="MongoDBVectorSearchIndexDefinition"/>
    /// exactly once.
    /// </summary>
    public static BsonDocument BuildDefinition(MongoDBVectorSearchIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var fields = new BsonArray
        {
            new BsonDocument
            {
                { "type", "vector" },
                { "path", definition.VectorFieldName },
                { "numDimensions", definition.VectorDimensions },
                { "similarity", definition.Similarity ?? "cosine" },
            },
        };
        fields.AddRange(definition.FilterFieldPaths.Select(
            path => new BsonDocument { { "type", "filter" }, { "path", path } }));
        return new BsonDocument("fields", fields);
    }
}
