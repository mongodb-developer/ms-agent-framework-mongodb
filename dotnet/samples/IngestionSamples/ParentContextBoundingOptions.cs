namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// Deterministic character-based bounds applied to <see cref="ParentDocumentRetriever"/>'s hydrated parent content.
/// </summary>
/// <remarks>
/// These bounds are a documented, dependency-free proxy for a token budget: no tokenizer dependency is added, and
/// character count is used as a conservative, deterministic stand-in. A real deployment with an actual tokenizer
/// available could substitute a token-based bound ahead of these character bounds. Bounding is always applied after
/// score ordering, de-duplication, and source attribution are fully finalized by <see cref="ParentDocumentRetriever"/>
/// -- only each result's content may be shortened, and lower-ranked parents may be entirely omitted once the total
/// budget is exhausted, but the set and order of parents considered is never affected by these bounds themselves.
/// </remarks>
public sealed record ParentContextBoundingOptions
{
    /// <summary>Gets the maximum number of characters returned for any single parent's content. Must be positive.</summary>
    public int MaxCharactersPerParent { get; init; } = 2000;

    /// <summary>
    /// Gets the maximum total number of characters summed across every returned parent's (possibly already
    /// per-parent-truncated) content. Must be positive.
    /// </summary>
    public int MaxTotalContextCharacters { get; init; } = 8000;

    /// <summary>Validates both bounds are positive, throwing <see cref="IngestionValidationException"/> otherwise.</summary>
    public void Validate()
    {
        if (MaxCharactersPerParent <= 0)
        {
            throw new IngestionValidationException($"{nameof(MaxCharactersPerParent)} must be positive.");
        }

        if (MaxTotalContextCharacters <= 0)
        {
            throw new IngestionValidationException($"{nameof(MaxTotalContextCharacters)} must be positive.");
        }
    }
}
