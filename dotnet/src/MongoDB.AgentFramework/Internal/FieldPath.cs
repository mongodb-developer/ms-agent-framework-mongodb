using MongoDB.Bson;

namespace MongoDB.AgentFramework.Internal;

internal static class FieldPath
{
    /// <summary>
    /// The reserved alias the vector/search pipelines use to carry MongoDB's native score under, shared by
    /// <see cref="Validate"/> (which rejects any configured field path that collides with it) and by
    /// <c>RAGPipelineBuilder</c>/<c>MongoDBRAGProvider</c> (which write and read it, respectively), so the literal
    /// is defined exactly once.
    /// </summary>
    internal const string ReservedScoreAlias = "_ragScore";

    /// <summary>
    /// The reserved alias <see cref="MongoDBSearchMode.HybridRrf"/>'s pipeline uses to carry <c>$rankFusion</c>'s
    /// optional <c>scoreDetails</c> diagnostic metadata under, when
    /// <see cref="MongoDBRAGProviderOptions.IncludeScoreDetails"/> is enabled. Shares <see cref="Validate"/>'s
    /// collision guard with <see cref="ReservedScoreAlias"/> for the same reason.
    /// </summary>
    internal const string ReservedScoreDetailsAlias = "_ragScoreDetails";

    public static string Validate(string path, string optionName = "field path")
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new MongoDBConfigurationException($"{optionName} must not be empty.");
        }

        if (path.Contains('\0'))
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must not contain null bytes.");
        }

        string[] segments = path.Split('.');
        if (segments.Any(static segment => segment.Length == 0))
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must not contain empty segments.");
        }

        if (segments.Any(static segment => segment.StartsWith('$')))
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must not contain '$' field segments.");
        }

        if (segments.Any(static segment =>
                segment == "$[]" ||
                segment.All(char.IsDigit)))
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must not use positional array syntax.");
        }

        if (segments.Contains(ReservedScoreAlias, StringComparer.Ordinal) ||
            segments.Contains(ReservedScoreDetailsAlias, StringComparer.Ordinal))
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must not collide with reserved alias '{ReservedScoreAlias}' or " +
                $"'{ReservedScoreDetailsAlias}'.");
        }

        return path;
    }

    public static BsonValue Resolve(BsonDocument document, string path)
    {
        if (!TryResolve(document, path, out BsonValue? value))
        {
            throw new MongoDBMappingException(
                $"Required field '{path}' is missing from the result.");
        }

        return value!;
    }

    /// <summary>
    /// Resolves a validated field path without throwing when a segment is missing or an intermediate value is not
    /// a document, so optional fields (source name/URL, metadata) can resolve to <see langword="null"/>/empty
    /// values instead of failing the whole mapping.
    /// </summary>
    public static bool TryResolve(BsonDocument document, string path, out BsonValue? value)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(path);

        BsonValue current = document;
        foreach (string segment in path.Split('.'))
        {
            if (!current.IsBsonDocument ||
                !current.AsBsonDocument.TryGetValue(segment, out BsonValue? next))
            {
                value = null;
                return false;
            }

            current = next;
        }

        value = current;
        return true;
    }
}
