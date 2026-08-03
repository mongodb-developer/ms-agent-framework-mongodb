using System.Globalization;
using System.Security.Cryptography;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// Derives stable, deterministic document/chunk/parent IDs from canonical source identity alone (tenant, source,
/// and positional index) -- never from a random GUID or wall-clock timestamp -- so re-ingesting the same source
/// content is idempotent (docs/spec/features/ingestion.md) and produces exactly the same IDs every run.
/// </summary>
public static class DeterministicId
{
    /// <summary>Derives a stable child chunk ID from tenant, source, and positional chunk index.</summary>
    public static string ForChunk(string tenantId, string sourceId, int chunkIndex)
    {
        RequireText(tenantId, nameof(tenantId));
        RequireText(sourceId, nameof(sourceId));
        if (chunkIndex < 0)
        {
            throw new IngestionValidationException($"{nameof(chunkIndex)} must not be negative.");
        }

        return "chunk_" + Hash(tenantId, sourceId, "chunk", chunkIndex.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Derives a stable parent document ID from tenant and source identity, for parent-document RAG.</summary>
    public static string ForParent(string tenantId, string sourceId)
    {
        RequireText(tenantId, nameof(tenantId));
        RequireText(sourceId, nameof(sourceId));
        return "parent_" + Hash(tenantId, sourceId, "parent");
    }

    // Fields are combined via CanonicalFraming.Frame rather than delimiter-joined concatenation, so no combination
    // of tenantId/sourceId/tag/index values -- including ones containing embedded delimiter or control characters
    // -- can be reinterpreted as a different split of fields and collide onto the same ID.
    private static string Hash(params string[] fields) =>
        Convert.ToHexStringLower(SHA256.HashData(CanonicalFraming.Frame(fields)));

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IngestionValidationException($"{name} must not be empty.");
        }
    }
}
