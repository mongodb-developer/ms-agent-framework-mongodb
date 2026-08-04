namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A bounded, deterministic sliding-window/overlap chunker. Given the same content and options it always produces
/// the same ordered chunk list (required so <see cref="DeterministicId"/>'s positional chunk IDs stay stable across
/// reruns), and it never produces an empty or duplicate chunk.
/// </summary>
public static class DocumentChunker
{
    /// <summary>Splits <paramref name="content"/> into bounded, non-empty, de-duplicated, ordered chunks.</summary>
    public static IReadOnlyList<string> Chunk(string content, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        string trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        // OverlapSize < WindowSize is enforced by Validate(), so step is always positive and every iteration
        // strictly advances -- no infinite loop is possible regardless of caller-supplied values.
        int step = options.WindowSize - options.OverlapSize;
        var chunks = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int position = 0;
        while (position < trimmed.Length)
        {
            int length = Math.Min(options.WindowSize, trimmed.Length - position);
            string candidate = trimmed.Substring(position, length).Trim();

            // Skip whitespace-only windows and de-duplicate globally (not just against the immediately preceding
            // chunk), since overlap can otherwise reproduce an identical window at the end of short content, and
            // repeated passages elsewhere in the source could otherwise also collide on chunk identity.
            if (candidate.Length > 0 && seen.Add(candidate))
            {
                chunks.Add(candidate);
            }

            if (position + length >= trimmed.Length)
            {
                break;
            }

            position += step;
        }

        return chunks;
    }
}
