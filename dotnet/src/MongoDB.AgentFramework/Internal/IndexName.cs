using System.Text.RegularExpressions;

namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// Validates MongoDB Search and Vector Search index names against a bounded allowlist. Index names are configured
/// application state, never model output, but they still flow into every retrieval pipeline stage, so this
/// validator rejects control characters, operator-like syntax (a leading '$' or embedded braces/operators),
/// separators that have no meaning for an index name (dots, slashes, colons, semicolons, whitespace), and
/// excessively long names, while accepting the letters, digits, underscores, and hyphens that make up a valid
/// MongoDB Search/Vector Search index name.
/// </summary>
internal static class IndexName
{
    /// <summary>The maximum accepted index name length.</summary>
    public const int MaxLength = 128;

    // Must start with a letter or underscore (never a digit or hyphen) and contain only letters, digits,
    // underscores, and hyphens thereafter. This is intentionally narrower than the general MongoDB field-path
    // allowlist because an index name is never dotted, never positional, and never references a document field.
    private static readonly Regex AllowedPattern =
        new("^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string Validate(string value, string optionName = "index name")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MongoDBConfigurationException($"{optionName} must not be empty.");
        }

        if (value.Length > MaxLength)
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must not exceed {MaxLength} characters.");
        }

        if (value.Any(char.IsControl))
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must not contain control characters.");
        }

        if (!AllowedPattern.IsMatch(value))
        {
            throw new MongoDBConfigurationException(
                $"{optionName} must start with a letter or underscore and contain only letters, digits, " +
                "underscores, or hyphens.");
        }

        return value;
    }
}
