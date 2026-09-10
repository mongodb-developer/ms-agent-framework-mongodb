namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A deterministic, surrogate-pair-safe character truncation helper used by <see cref="ParentDocumentRetriever"/>
/// to enforce <see cref="ParentContextBoundingOptions"/>'s character bounds.
/// </summary>
internal static class BoundedTextTruncation
{
    /// <summary>
    /// Returns <paramref name="text"/> truncated to at most <paramref name="maxCharacters"/> UTF-16 code units,
    /// never splitting a UTF-16 surrogate pair. Returns <see cref="string.Empty"/> when
    /// <paramref name="maxCharacters"/> is not positive.
    /// </summary>
    public static string Truncate(string text, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxCharacters <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxCharacters)
        {
            return text;
        }

        int cutLength = maxCharacters;
        if (char.IsHighSurrogate(text[cutLength - 1]))
        {
            // Cutting exactly here would leave an orphaned high surrogate with no matching low surrogate at the end
            // of the truncated string; back off one further character so a supplementary-plane character is never
            // split in half.
            cutLength--;
        }

        return text[..cutLength];
    }
}
