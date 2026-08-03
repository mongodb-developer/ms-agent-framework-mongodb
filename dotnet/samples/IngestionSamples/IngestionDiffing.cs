namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// One candidate record awaiting the unchanged/changed/stale diff shared by
/// <see cref="IncrementalIngestionPipeline"/> and <see cref="ParentDocumentIngestionPipeline"/>.
/// </summary>
internal readonly record struct ChunkCandidate(
    string Id,
    string? ParentId,
    string RecordType,
    string Text,
    string Hash,
    bool NeedsEmbedding);

/// <summary>
/// The unchanged/changed/stale diff shared by both ingestion pipelines: a desired candidate whose ID/hash already
/// matches a stored record is unchanged and skipped; every other desired candidate must be (re-)written; every
/// currently stored ID no longer present in the desired set is stale and must be deleted.
/// </summary>
internal static class IngestionDiffing
{
    public static (IReadOnlyList<ChunkCandidate> ToWrite, int UnchangedCount, IReadOnlyList<string> StaleIds) Diff(
        IReadOnlyList<ChunkCandidate> desired,
        IReadOnlyDictionary<string, string> existing)
    {
        var toWrite = new List<ChunkCandidate>();
        int unchanged = 0;
        foreach (ChunkCandidate candidate in desired)
        {
            if (existing.TryGetValue(candidate.Id, out string? existingHash) && existingHash == candidate.Hash)
            {
                unchanged++;
            }
            else
            {
                toWrite.Add(candidate);
            }
        }

        var desiredIds = new HashSet<string>(desired.Select(static candidate => candidate.Id), StringComparer.Ordinal);
        string[] staleIds = [.. existing.Keys.Where(id => !desiredIds.Contains(id))];
        return (toWrite, unchanged, staleIds);
    }
}
