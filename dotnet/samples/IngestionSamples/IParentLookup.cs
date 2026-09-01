namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-local seam over the bounded parent-hydration lookup, isolating the second query so
/// <see cref="ParentDocumentRetriever"/>'s bounding/de-duplication logic is unit-testable offline. The production
/// implementation is <see cref="MongoParentLookup"/>. This is a fixed, single-method contract -- not an
/// unrestricted pipeline callback -- and every implementation MUST enforce the tenant scope itself rather than
/// trusting the caller-supplied parent ID list alone.
/// </summary>
public interface IParentLookup
{
    /// <summary>
    /// Looks up at most <paramref name="parentIds"/>'s count of parent records, requiring each to match
    /// <paramref name="tenantId"/>. A parent ID with no matching authorized record is simply omitted from the
    /// result rather than causing an error, since that is expected when a parent was deleted or belongs to a
    /// different tenant than the caller expected.
    /// </summary>
    Task<IReadOnlyList<ParentDocument>> FindParentsAsync(
        IReadOnlyList<string> parentIds,
        string tenantId,
        CancellationToken cancellationToken = default);
}
