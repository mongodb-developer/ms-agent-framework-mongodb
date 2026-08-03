using System.Security.Cryptography;
using System.Text;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// Computes a stable content hash used to detect whether a chunk's or parent's text changed since the last
/// ingestion run, so unchanged content can be safely skipped (docs/spec/features/ingestion.md's changed-document
/// upsert requirement) without re-embedding or rewriting it.
/// </summary>
public static class ContentHash
{
    /// <summary>Computes a stable, lowercase hex SHA-256 hash of <paramref name="content"/>'s UTF-8 bytes.</summary>
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
