using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework;

/// <summary>
/// An immutable, structured Atlas Search (full-text) index definition: the mapped text field paths that must be
/// text-searchable, plus an optional <see cref="MongoDBRAGFilter"/> whose referenced fields must be mapped
/// compatibly with the operator/value-type they are used with wherever the mapping is statically deterministic. A
/// dynamic Search mapping (<c>mappings.dynamic == true</c>) indexes every field automatically and provides no
/// per-field enumeration to validate against; per docs/spec/features/index-management.md, this is a documented
/// validation limitation rather than something an index manager should silently paper over by inventing an
/// automatic mapping change -- see <see cref="MongoDBIndexComparison"/> and the validating methods on
/// <see cref="MongoDBRAGIndexManager"/> for how a dynamic mapping is surfaced.
/// </summary>
public sealed record MongoDBSearchIndexDefinition
{
    /// <summary>Initializes an immutable Search index definition.</summary>
    /// <param name="indexName">The Search index name.</param>
    /// <param name="textFieldNames">The text field paths that must map to a text-searchable type.</param>
    /// <param name="mandatoryFilter">
    /// The optional mandatory filter whose referenced fields must be mapped compatibly with their operator/value
    /// category wherever the Search mapping is statically deterministic (non-dynamic). Mirrors
    /// <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>.
    /// </param>
    public MongoDBSearchIndexDefinition(
        string indexName,
        IReadOnlyList<string> textFieldNames,
        MongoDBRAGFilter? mandatoryFilter = null)
    {
        IndexName = Internal.IndexName.Validate(indexName, nameof(indexName));
        if (textFieldNames is null || textFieldNames.Count == 0)
        {
            throw new MongoDBConfigurationException(
                $"{nameof(textFieldNames)} must contain at least one field path.");
        }

        foreach (string field in textFieldNames)
        {
            FieldPath.Validate(field, nameof(textFieldNames));
        }

        TextFieldNames = [.. textFieldNames];
        MandatoryFilter = mandatoryFilter;
    }

    /// <summary>Gets the Search index name.</summary>
    public string IndexName { get; }

    /// <summary>Gets the text field paths that must map to a text-searchable type.</summary>
    public IReadOnlyList<string> TextFieldNames { get; }

    /// <summary>
    /// Gets the optional mandatory filter whose referenced fields must be mapped compatibly wherever the Search
    /// mapping is statically deterministic.
    /// </summary>
    public MongoDBRAGFilter? MandatoryFilter { get; }
}
