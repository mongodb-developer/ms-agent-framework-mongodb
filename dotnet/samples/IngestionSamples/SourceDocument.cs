namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-local, ingestion-neutral representation of one source document to ingest, identified by its canonical
/// source identity (<see cref="SourceId"/>) within a tenant. Deterministic chunk/parent IDs and content hashes are
/// derived from these fields, so the same logical source re-ingested with unchanged content always produces the
/// same IDs and is safely skipped (docs/spec/features/ingestion.md's idempotent-rerun requirement).
/// </summary>
/// <param name="TenantId">The mandatory tenant/authorization scope this document belongs to.</param>
/// <param name="SourceId">
/// The canonical, stable identity of the source (for example a file path or URL). Must be stable across reruns;
/// changing it is treated as ingesting a different source, not updating this one.
/// </param>
/// <param name="Content">The raw source text to chunk and embed.</param>
/// <param name="Title">An optional source title used for citation/attribution.</param>
/// <param name="Url">An optional source URL used for citation/attribution.</param>
public sealed record SourceDocument(
    string TenantId,
    string SourceId,
    string Content,
    string? Title = null,
    string? Url = null)
{
    /// <summary>Validates the required fields without contacting MongoDB.</summary>
    public void Validate()
    {
        RequireText(TenantId, nameof(TenantId));
        RequireText(SourceId, nameof(SourceId));
        ArgumentNullException.ThrowIfNull(Content);
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IngestionValidationException($"{name} must not be empty.");
        }
    }
}
