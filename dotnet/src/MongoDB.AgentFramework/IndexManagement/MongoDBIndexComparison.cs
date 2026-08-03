using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework;

/// <summary>
/// The result of comparing an inspected MongoDB Search/Vector Search index against an expected definition.
/// Comparison is semantic and order-insensitive (unordered field/filter-path sets, tolerant of server-added
/// defaults) and distinguishes an actionable mismatch (something the caller must explicitly fix, for example a
/// wrong vector dimension) from a merely informational compatible difference (for example an extra server-default
/// key that does not change retrieval behavior), per docs/spec/features/index-management.md.
/// </summary>
public sealed record MongoDBIndexComparison
{
    /// <summary>A shared, reusable "fully compatible, no differences" result.</summary>
    public static readonly MongoDBIndexComparison Compatible = new([], []);

    /// <summary>Initializes a comparison result.</summary>
    /// <param name="mismatches">
    /// Actionable differences the caller must explicitly resolve (for example through <c>UpdateIndexAsync</c>).
    /// Empty when the index is fully compatible with the expected definition.
    /// </param>
    /// <param name="compatibleDifferences">
    /// Informational, non-actionable differences (for example server-added defaults) that do not affect
    /// retrieval correctness and never need to be resolved.
    /// </param>
    public MongoDBIndexComparison(
        IReadOnlyList<string> mismatches,
        IReadOnlyList<string>? compatibleDifferences = null)
    {
        Mismatches = ImmutableCollections.Snapshot(
            mismatches ?? throw new ArgumentNullException(nameof(mismatches)));
        CompatibleDifferences = ImmutableCollections.Snapshot(compatibleDifferences);
    }

    /// <summary>Gets whether the index is compatible with the expected definition (no actionable mismatches).</summary>
    public bool IsCompatible => Mismatches.Count == 0;

    /// <summary>Gets the actionable mismatches, empty when <see cref="IsCompatible"/> is <see langword="true"/>.</summary>
    public IReadOnlyList<string> Mismatches { get; }

    /// <summary>Gets informational, non-actionable differences that never need to be resolved.</summary>
    public IReadOnlyList<string> CompatibleDifferences { get; }
}
