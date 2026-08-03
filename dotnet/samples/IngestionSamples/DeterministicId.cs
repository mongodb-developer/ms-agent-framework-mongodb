using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

        return "chunk_" + Hash($"{tenantId}\u001f{sourceId}\u001fchunk\u001f{chunkIndex.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Derives a stable parent document ID from tenant and source identity, for parent-document RAG.</summary>
    public static string ForParent(string tenantId, string sourceId)
    {
        RequireText(tenantId, nameof(tenantId));
        RequireText(sourceId, nameof(sourceId));
        return "parent_" + Hash($"{tenantId}\u001f{sourceId}\u001fparent");
    }

    private static string Hash(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IngestionValidationException($"{name} must not be empty.");
        }
    }
}
