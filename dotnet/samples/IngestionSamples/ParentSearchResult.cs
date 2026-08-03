namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// One bounded, de-duplicated, attributed parent result returned by <see cref="ParentDocumentRetriever"/>: the
/// hydrated parent content plus the best (highest-ranked) matching child chunk's score and ID for diagnostics.
/// </summary>
public sealed record ParentSearchResult(
    string ParentId,
    string Content,
    double BestChildScore,
    string BestChildId,
    string? SourceName,
    string? SourceUrl);
