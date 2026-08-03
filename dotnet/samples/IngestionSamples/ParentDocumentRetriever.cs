using MongoDB.Bson;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// The sample-only parent-document retrieval pattern (docs/spec/features/rag.md's "Parent-document retrieval"
/// section): searches small embedded child chunks through an <see cref="IChildChunkSearcher"/> (constrained to
/// authorized child records by the searcher's own configuration), then performs one bounded, de-duplicated,
/// tenant-scoped parent hydration lookup through an <see cref="IParentLookup"/>. There is no caller-supplied
/// pipeline callback: both steps are fixed methods on fixed seams, and every bound (child candidates via the
/// searcher's own <c>TopK</c>, parents per query via <see cref="_maxParents"/>, lookup fan-out via the same bound)
/// is enforced before the parent lookup query is ever issued.
/// </summary>
public sealed class ParentDocumentRetriever
{
    private readonly IChildChunkSearcher _childSearcher;
    private readonly IParentLookup _parentLookup;
    private readonly string _tenantId;
    private readonly int _maxParents;
    private readonly string _parentIdMetadataFieldName;

    /// <summary>Initializes a retriever over injected, caller-owned search and lookup seams.</summary>
    /// <param name="childSearcher">
    /// The child chunk searcher. Must already be configured to constrain retrieval to authorized child records.
    /// </param>
    /// <param name="parentLookup">The bounded, tenant-enforcing parent hydration lookup.</param>
    /// <param name="tenantId">The mandatory tenant scope every hydrated parent must satisfy.</param>
    /// <param name="maxParents">
    /// The maximum number of distinct parents returned, and the fan-out cap applied to the parent lookup query.
    /// Must be positive.
    /// </param>
    /// <param name="parentIdMetadataFieldName">
    /// The <see cref="MongoDBRAGResult.Metadata"/> key each child result's parent ID is read from. The searcher's
    /// own <c>MongoDBRAGProviderOptions.MetadataFieldNames</c> must include the underlying field path (typically
    /// <c>"parent_id"</c>) for this value to be populated.
    /// </param>
    public ParentDocumentRetriever(
        IChildChunkSearcher childSearcher,
        IParentLookup parentLookup,
        string tenantId,
        int maxParents = 10,
        string parentIdMetadataFieldName = "parent_id")
    {
        _childSearcher = childSearcher ?? throw new ArgumentNullException(nameof(childSearcher));
        _parentLookup = parentLookup ?? throw new ArgumentNullException(nameof(parentLookup));
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new IngestionValidationException($"{nameof(tenantId)} must not be empty.");
        }

        if (maxParents <= 0)
        {
            throw new IngestionValidationException($"{nameof(maxParents)} must be positive.");
        }

        if (string.IsNullOrWhiteSpace(parentIdMetadataFieldName))
        {
            throw new IngestionValidationException($"{nameof(parentIdMetadataFieldName)} must not be empty.");
        }

        _tenantId = tenantId;
        _maxParents = maxParents;
        _parentIdMetadataFieldName = parentIdMetadataFieldName;
    }

    /// <summary>
    /// Searches child chunks for <paramref name="query"/>, then hydrates at most <see cref="_maxParents"/>
    /// distinct, best-scoring parents with source attribution, ordered by descending best child score.
    /// </summary>
    public async Task<IReadOnlyList<ParentSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MongoDBRAGResult> childResults = await _childSearcher
            .SearchAsync(query, cancellationToken)
            .ConfigureAwait(false);

        // De-duplicate parent IDs while keeping only the first (best-ranked, since results are already ordered by
        // score) child match per parent, and bound fan-out to _maxParents before the parent lookup query is ever
        // issued.
        var bestChildByParent = new Dictionary<string, MongoDBRAGResult>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (MongoDBRAGResult child in childResults)
        {
            if (!child.Metadata.TryGetValue(_parentIdMetadataFieldName, out BsonValue? parentIdValue) ||
                parentIdValue is null || parentIdValue.IsBsonNull)
            {
                // Defensive: a child record missing its parent linkage is skipped rather than failing the whole
                // retrieval, since every other matching child should still be able to hydrate its own parent.
                continue;
            }

            string parentId = parentIdValue.AsString;
            if (bestChildByParent.TryAdd(parentId, child))
            {
                order.Add(parentId);
                if (order.Count >= _maxParents)
                {
                    break;
                }
            }
        }

        if (order.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ParentDocument> parents = await _parentLookup
            .FindParentsAsync(order, _tenantId, cancellationToken)
            .ConfigureAwait(false);
        var parentsById = parents.ToDictionary(static parent => parent.ParentId, StringComparer.Ordinal);

        var results = new List<ParentSearchResult>(order.Count);
        foreach (string parentId in order)
        {
            // A parent absent from the authorized lookup result (deleted, or excluded by the tenant scope the
            // lookup itself enforces) is simply omitted rather than surfaced as a partial/unauthorized result.
            if (!parentsById.TryGetValue(parentId, out ParentDocument? parent))
            {
                continue;
            }

            MongoDBRAGResult bestChild = bestChildByParent[parentId];
            results.Add(new ParentSearchResult(
                parentId,
                parent.Content,
                bestChild.Score,
                bestChild.Id,
                parent.SourceName ?? bestChild.SourceName,
                parent.SourceUrl ?? bestChild.SourceUrl));
        }

        return results;
    }
}
