using System.Collections.ObjectModel;

namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// Builds a truly immutable snapshot of a sequence for a public <see cref="IReadOnlyList{T}"/>-typed property.
/// A C# collection expression (<c>[.. source]</c>) targeting <see cref="IReadOnlyList{T}"/> is compiled as a plain
/// array, which a caller can cast back to <c>T[]</c> (or, for a <see cref="List{T}"/>-backed property, back to
/// <see cref="List{T}"/>) and mutate in place -- silently invalidating a definition or comparison result that was
/// documented as immutable. <see cref="Snapshot{T}"/> instead defensively copies the source sequence into a
/// private <see cref="List{T}"/> that is never itself exposed, then wraps it in a <see cref="ReadOnlyCollection{T}"/>:
/// the concrete instance returned is not castable back to <c>T[]</c> or <see cref="List{T}"/>, and every mutating
/// <see cref="IList{T}"/> member on it throws <see cref="NotSupportedException"/>.
/// </summary>
internal static class ImmutableCollections
{
    /// <summary>
    /// Returns a defensive, non-castable, non-mutable snapshot of <paramref name="source"/> (empty when
    /// <paramref name="source"/> is <see langword="null"/>). Later mutation of <paramref name="source"/> itself
    /// (when it is a mutable collection the caller still holds a reference to) never affects the returned snapshot.
    /// </summary>
    internal static IReadOnlyList<T> Snapshot<T>(IEnumerable<T>? source) =>
        new ReadOnlyCollection<T>(source is null ? [] : [.. source]);
}
