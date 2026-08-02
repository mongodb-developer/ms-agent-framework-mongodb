using MongoDB.Bson;

namespace MongoDB.AgentFramework.Internal;

internal static class FieldPath
{
    private const string ReservedScoreAlias = "_ragScore";

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

        if (segments.Contains(ReservedScoreAlias, StringComparer.Ordinal))
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must not collide with reserved alias '{ReservedScoreAlias}'.");
        }

        return path;
    }

    public static BsonValue Resolve(BsonDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(path);

        BsonValue current = document;
        foreach (string segment in path.Split('.'))
        {
            if (!current.IsBsonDocument ||
                !current.AsBsonDocument.TryGetValue(segment, out BsonValue? next))
            {
                throw new MongoDBMappingException(
                    $"Required field '{path}' is missing from the result.");
            }

            current = next;
        }

        return current;
    }
}
