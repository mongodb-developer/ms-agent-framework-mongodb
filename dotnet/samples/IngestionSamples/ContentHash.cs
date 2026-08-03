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
        return ComputeBytes(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Computes a stable, lowercase hex SHA-256 hash over multiple fields using <see cref="CanonicalFraming"/>
    /// rather than delimiter-joined concatenation, so no combination of field values -- including ones containing
    /// embedded delimiter or other control characters, or a <see langword="null"/> field versus an empty one -- can
    /// produce the same hash for a logically different combination of fields.
    /// </summary>
    public static string ComputeFramed(params string?[] fields) => ComputeBytes(CanonicalFraming.Frame(fields));

    private static string ComputeBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
