namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// Configurable bounded sliding-window/overlap settings for <see cref="DocumentChunker"/>. Validated eagerly so a
/// misconfigured window can never cause an infinite or empty chunking loop.
/// </summary>
public sealed record ChunkingOptions
{
    /// <summary>Gets the maximum character length of one chunk. Must be positive.</summary>
    public int WindowSize { get; init; } = 500;

    /// <summary>
    /// Gets the number of characters consecutive chunks overlap by. Must be non-negative and strictly less than
    /// <see cref="WindowSize"/> so each window always advances.
    /// </summary>
    public int OverlapSize { get; init; } = 50;

    /// <summary>Validates this instance without contacting MongoDB.</summary>
    public void Validate()
    {
        if (WindowSize <= 0)
        {
            throw new IngestionValidationException($"{nameof(WindowSize)} must be positive.");
        }

        if (OverlapSize < 0)
        {
            throw new IngestionValidationException($"{nameof(OverlapSize)} must not be negative.");
        }

        if (OverlapSize >= WindowSize)
        {
            throw new IngestionValidationException(
                $"{nameof(OverlapSize)} must be less than {nameof(WindowSize)}.");
        }
    }
}
