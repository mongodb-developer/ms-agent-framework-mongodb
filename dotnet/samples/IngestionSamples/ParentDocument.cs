namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>A minimal parent record read back by an <see cref="IParentLookup"/> during parent hydration.</summary>
public sealed record ParentDocument(
    string ParentId,
    string Content,
    string? SourceName,
    string? SourceUrl);
