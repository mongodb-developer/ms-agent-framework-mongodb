using MongoDB.Bson;

namespace MongoDB.AgentFramework.Internal.IndexManagement;

/// <summary>
/// Pure, non-throwing semantic comparison between an inspected Atlas Search (full-text) index definition and an
/// expected <see cref="MongoDBSearchIndexDefinition"/>. Shared by <see cref="MongoDBRAGProvider"/>'s FullText and
/// Hybrid text-branch validation and by <see cref="MongoDBRAGIndexManager"/>'s explicit facade, so this mapping
/// resolution is implemented exactly once. A dynamic Search mapping (<c>mappings.dynamic == true</c>) indexes
/// every field automatically and provides no per-field enumeration to validate against; this is a documented
/// limitation (see <see cref="SearchIndexComparisonResult.DynamicMappingFieldsUnverified"/>), not something this
/// class invents an automatic mapping change to work around.
/// </summary>
internal static class SearchIndexEquivalence
{
    /// <summary>
    /// Compares <paramref name="definition"/> (an inspected index's <c>latestDefinition</c>/<c>definition</c>
    /// document) against <paramref name="expected"/>.
    /// </summary>
    public static SearchIndexComparisonResult Compare(BsonDocument definition, MongoDBSearchIndexDefinition expected)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(expected);

        BsonDocument mappings = definition.GetValue("mappings", new BsonDocument()).AsBsonDocument;
        var mismatches = new List<string>();
        bool dynamicMappingFieldsUnverified = false;
        if (IsDynamicMappingEnabled(mappings, expected.IndexName, mismatches))
        {
            // A dynamic mapping indexes every field automatically, so text-field mapping cannot be statically
            // disproven here; it is treated as satisfied for the purposes of Mismatches, but callers must still
            // know per-field mandatory-filter compatibility could not be checked (see below).
            dynamicMappingFieldsUnverified = expected.MandatoryFilter is not null &&
                RAGFilterFieldReferences.Enumerate(expected.MandatoryFilter).Count > 0;
        }
        else
        {
            BsonDocument fields = mappings.GetValue("fields", new BsonDocument()).AsBsonDocument;
            foreach (string textField in expected.TextFieldNames)
            {
                IReadOnlyList<BsonDocument> definitions = ResolveFieldMappingDefinitions(
                    fields, textField, expected.IndexName, mismatches);
                if (definitions.Count == 0)
                {
                    mismatches.Add(
                        $"Search index '{expected.IndexName}' does not map configured field '{textField}'.");
                }
                else if (!definitions.Any(IsTextCompatible))
                {
                    string types = string.Join(", ", definitions.Select(d => d.GetValue("type", "").AsString));
                    mismatches.Add(
                        $"Search index '{expected.IndexName}' maps field '{textField}' to '{types}', none of " +
                        "which are text-searchable.");
                }
            }

            foreach (FilterFieldReference reference in RAGFilterFieldReferences.Enumerate(expected.MandatoryFilter))
            {
                IReadOnlyList<BsonDocument> definitions = ResolveFieldMappingDefinitions(
                    fields, reference.FieldPath, expected.IndexName, mismatches);
                if (definitions.Count == 0)
                {
                    mismatches.Add(
                        $"Search index '{expected.IndexName}' does not map mandatory-filter field " +
                        $"'{reference.FieldPath}'.");
                    continue;
                }

                foreach (FilterValueCategory valueCategory in BsonValueCategories.Flags(reference.ValueCategories))
                {
                    if (!definitions.Any(d => IsFilterValueCategoryCompatible(d, reference.Category, valueCategory)))
                    {
                        string types = string.Join(", ", definitions.Select(d => d.GetValue("type", "").AsString));
                        mismatches.Add(
                            $"Search index '{expected.IndexName}' maps mandatory-filter field " +
                            $"'{reference.FieldPath}' to '{types}', which is not compatible with a " +
                            $"{reference.Category} filter over a {valueCategory} value.");
                    }
                }
            }
        }

        return new SearchIndexComparisonResult(
            new MongoDBIndexComparison(mismatches),
            dynamicMappingFieldsUnverified);
    }

    /// <summary>
    /// Checks whether <paramref name="index"/>'s reported <c>type</c> is <c>"search"</c>, returning the actual
    /// type when it is not (for a mismatch message) or <see langword="null"/> when it matches.
    /// </summary>
    public static string? CheckIndexType(BsonDocument index)
    {
        string type = index.GetValue("type", "").AsString;
        return string.Equals(type, "search", StringComparison.OrdinalIgnoreCase) ? null : type;
    }

    /// <summary>
    /// Checks index type, compares an already-found <paramref name="index"/> against <paramref name="expected"/>,
    /// and (when <paramref name="requireReady"/>) requires <c>READY</c>/queryable status -- throwing
    /// <see cref="MongoDBIndexMismatchException"/>/<see cref="MongoDBIndexNotReadyException"/> on failure. Shared
    /// by <see cref="MongoDBRAGProvider"/> and <see cref="MongoDBRAGIndexManager"/> so this throw-shape is
    /// implemented exactly once.
    /// </summary>
    public static SearchIndexComparisonResult Validate(
        BsonDocument index, MongoDBSearchIndexDefinition expected, bool requireReady)
    {
        if (CheckIndexType(index) is { } actualType)
        {
            throw new MongoDBIndexMismatchException(
                $"Search index '{expected.IndexName}' is not a Search index (found type '{actualType}'); " +
                "FullText/Hybrid requires a Search index, not a Vector Search index.");
        }

        // A terminal build failure is checked before comparing definitions (and regardless of requireReady): a
        // failed index never becomes ready on its own, so this is always an actionable, non-transient problem --
        // never something bounded polling should retry until its deadline (see MongoDBIndexFailedException).
        if (MongoDBSearchIndexes.Classify(index) == MongoDBIndexStatus.Failed)
        {
            throw new MongoDBIndexFailedException(
                $"Search index '{expected.IndexName}' build failed and requires explicit repair (update or " +
                "recreate); it will never become ready on its own.");
        }

        SearchIndexComparisonResult result = Compare(MongoDBSearchIndexes.GetDefinition(index), expected);
        if (!result.Comparison.IsCompatible)
        {
            throw new MongoDBIndexMismatchException(
                $"Search index '{expected.IndexName}' does not match the required definition: " +
                string.Join("; ", result.Comparison.Mismatches));
        }

        if (requireReady && MongoDBSearchIndexes.Classify(index) is not MongoDBIndexStatus.Ready)
        {
            throw new MongoDBIndexNotReadyException($"Search index '{expected.IndexName}' is not queryable.");
        }

        return result;
    }

    /// <summary>
    /// Builds a non-dynamic Search index definition document (the <c>mappings</c> object only) satisfying every
    /// <see cref="MongoDBSearchIndexDefinition.TextFieldNames"/> and <see cref="MongoDBSearchIndexDefinition.MandatoryFilter"/>-referenced
    /// field. A dotted field path (for example <c>"metadata.tenant_id"</c>) is expressed through nested
    /// <c>type: "document"</c>/<c>fields</c> mapping objects rather than a literal dotted key -- Atlas Search has
    /// no dotted-key mapping shape, only nested <c>document</c> fields, matching exactly what
    /// <see cref="ResolveFieldMappingDefinitions"/> resolves back on the read side. When the same terminal field
    /// path is required by both a text field and one or more mandatory-filter value categories (or by multiple
    /// heterogeneous value categories from a single membership filter), every required type is merged and
    /// deduplicated at that path, emitting a single mapping document when exactly one type is required or a
    /// multi-type mapping array (Atlas Search's supported shape for mapping one field to several type
    /// definitions simultaneously) when more than one is. Used by both create and update so the mapping shape is
    /// derived from <see cref="MongoDBSearchIndexDefinition"/> exactly once.
    /// </summary>
    public static BsonDocument BuildDefinition(MongoDBSearchIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var requiredTypesByPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string textField in definition.TextFieldNames)
        {
            AddRequiredType(requiredTypesByPath, textField, "string");
        }

        foreach (FilterFieldReference reference in RAGFilterFieldReferences.Enumerate(definition.MandatoryFilter))
        {
            foreach (FilterValueCategory valueCategory in BsonValueCategories.Flags(reference.ValueCategories))
            {
                AddRequiredType(
                    requiredTypesByPath, reference.FieldPath, FilterValueSearchType(reference.Category, valueCategory));
            }
        }

        var fields = new BsonDocument();
        foreach ((string path, List<string> types) in requiredTypesByPath)
        {
            SetFieldMapping(fields, path, types);
        }

        return new BsonDocument(
            "mappings",
            new BsonDocument { { "dynamic", false }, { "fields", fields } });
    }

    /// <summary>Records that <paramref name="path"/> requires <paramref name="type"/>, de-duplicating repeats.</summary>
    private static void AddRequiredType(Dictionary<string, List<string>> requiredTypesByPath, string path, string type)
    {
        if (!requiredTypesByPath.TryGetValue(path, out List<string>? types))
        {
            types = [];
            requiredTypesByPath[path] = types;
        }

        if (!types.Contains(type, StringComparer.Ordinal))
        {
            types.Add(type);
        }
    }

    /// <summary>Maps a mandatory-filter field's operator/BSON value category to the Atlas Search field type that satisfies it.</summary>
    private static string FilterValueSearchType(FilterOperatorCategory operatorCategory, FilterValueCategory valueCategory) =>
        operatorCategory switch
        {
            FilterOperatorCategory.Range => valueCategory switch
            {
                FilterValueCategory.Number => "number",
                FilterValueCategory.Date => "date",
                _ => throw new MongoDBConfigurationException(
                    $"A range filter over a {valueCategory} value has no supported Search field type."),
            },
            _ => valueCategory switch
            {
                FilterValueCategory.String => "token",
                FilterValueCategory.Boolean => "boolean",
                FilterValueCategory.Number => "number",
                FilterValueCategory.Date => "date",
                FilterValueCategory.ObjectId => "objectId",
                FilterValueCategory.Uuid => "uuid",
                _ => throw new MongoDBConfigurationException(
                    $"A filter value of category {valueCategory} has no supported Search field type."),
            },
        };

    /// <summary>
    /// Sets <paramref name="path"/>'s mapping within <paramref name="root"/>, creating (or reusing) nested
    /// <c>type: "document"</c> mapping objects for every intermediate dotted segment. Fails actionably rather than
    /// silently overwriting or corrupting a mapping if <paramref name="path"/> conflicts with another already-set
    /// field (for example one configured field at <c>"a"</c> and another at <c>"a.b"</c>), since a single field
    /// cannot simultaneously be a leaf value and a nested document.
    /// </summary>
    private static void SetFieldMapping(BsonDocument root, string path, IReadOnlyList<string> types)
    {
        string[] segments = path.Split('.');
        BsonDocument currentFields = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string segment = segments[i];
            if (currentFields.TryGetValue(segment, out BsonValue? existing))
            {
                if (existing is BsonDocument nested && nested.GetValue("type", "").AsString == "document")
                {
                    currentFields = nested["fields"].AsBsonDocument;
                    continue;
                }

                throw new MongoDBConfigurationException(
                    $"Field path '{path}' conflicts with another configured field mapped directly at " +
                    $"'{string.Join('.', segments[..(i + 1)])}'.");
            }

            var document = new BsonDocument { { "type", "document" }, { "fields", new BsonDocument() } };
            currentFields[segment] = document;
            currentFields = document["fields"].AsBsonDocument;
        }

        string terminal = segments[^1];
        if (currentFields.TryGetValue(terminal, out BsonValue? terminalExisting) &&
            terminalExisting is BsonDocument { } terminalDocument &&
            terminalDocument.GetValue("type", "").AsString == "document")
        {
            throw new MongoDBConfigurationException(
                $"Field path '{path}' conflicts with another configured field mapped as a nested path under it.");
        }

        currentFields[terminal] = types.Count == 1
            ? new BsonDocument("type", types[0])
            : new BsonArray(types.Select(static type => new BsonDocument("type", type)));
    }

    /// <summary>
    /// Determines whether <c>mappings.dynamic</c> enables automatic field indexing. Atlas Search accepts either a
    /// plain boolean or an object form (for example selecting a named type set); both mean "every field is
    /// indexed automatically". Any other shape is not a documented "dynamic" form and is recorded as an actionable
    /// mismatch rather than silently coerced by <see cref="BsonValue.ToBoolean"/> truthiness rules.
    /// </summary>
    private static bool IsDynamicMappingEnabled(BsonDocument mappings, string indexName, List<string> mismatches)
    {
        if (!mappings.TryGetValue("dynamic", out BsonValue? dynamicValue))
        {
            return false;
        }

        switch (dynamicValue)
        {
            case BsonBoolean boolean:
                return boolean.Value;
            case BsonDocument:
                return true;
            default:
                mismatches.Add(
                    $"Search index '{indexName}' has an unrecognized 'mappings.dynamic' shape " +
                    $"({dynamicValue.BsonType}); expected a boolean or an object.");
                return false;
        }
    }

    /// <summary>
    /// Resolves a possibly dotted field path through nested <c>type: "document"</c> mappings, returning every
    /// applicable type definition for the terminal field. Returns an empty list if the path is not mapped, and
    /// records an actionable mismatch (rather than silently treating it as unmapped) for a mapping shape that is
    /// neither a mapping object nor an array of mapping objects.
    /// </summary>
    private static IReadOnlyList<BsonDocument> ResolveFieldMappingDefinitions(
        BsonDocument fields, string path, string indexName, List<string> mismatches)
    {
        string[] segments = path.Split('.');
        BsonDocument currentFields = fields;
        for (int i = 0; i < segments.Length; i++)
        {
            if (!currentFields.TryGetValue(segments[i], out BsonValue? value))
            {
                return [];
            }

            IReadOnlyList<BsonDocument> definitions = ResolveFieldDefinitions(value, segments[i], indexName, mismatches);
            bool isLastSegment = i == segments.Length - 1;
            if (isLastSegment)
            {
                return definitions;
            }

            BsonDocument? nestedDocument = definitions.FirstOrDefault(
                d => string.Equals(d.GetValue("type", "").AsString, "document", StringComparison.OrdinalIgnoreCase));
            if (nestedDocument is null)
            {
                return [];
            }

            currentFields = nestedDocument.GetValue("fields", new BsonDocument()).AsBsonDocument;
        }

        return [];
    }

    /// <summary>Normalizes a single field-mapping value (a mapping object or an array of mapping objects).</summary>
    private static IReadOnlyList<BsonDocument> ResolveFieldDefinitions(
        BsonValue value, string fieldName, string indexName, List<string> mismatches)
    {
        switch (value)
        {
            case BsonDocument document:
                return [document];
            case BsonArray array:
                var definitions = new List<BsonDocument>();
                foreach (BsonValue element in array)
                {
                    if (element is BsonDocument document)
                    {
                        definitions.Add(document);
                    }
                    else
                    {
                        mismatches.Add(
                            $"Search index '{indexName}' has a multi-type mapping for field '{fieldName}' " +
                            $"containing a non-object entry ({element.BsonType}); expected an array of mapping " +
                            "objects.");
                    }
                }

                return definitions;
            default:
                mismatches.Add(
                    $"Search index '{indexName}' has an unrecognized mapping shape for field '{fieldName}' " +
                    $"({value.BsonType}); expected a mapping object or an array of mapping objects.");
                return [];
        }
    }

    /// <summary>
    /// A field is text-searchable if any applicable mapping definition is; only reject a field once every
    /// definition is confirmed non-text-compatible. Only <c>"string"</c> qualifies: <c>"token"</c> is exact-match
    /// only (never analyzed for relevance-ranked text search), and <c>"autocomplete"</c> is rejected too, because
    /// <see cref="RAGPipelineBuilder"/>'s <c>$search</c> stage always issues a <c>text</c> operator query and never
    /// an <c>autocomplete</c> one -- an <c>autocomplete</c>-only mapping would accept a definition the runtime
    /// query can never actually exercise as text-searchable.
    /// </summary>
    private static bool IsTextCompatible(BsonDocument fieldMapping) =>
        fieldMapping.GetValue("type", "").AsString is "string";

    /// <summary>
    /// Checks whether <paramref name="fieldMapping"/> is compatible with a single BSON value category used
    /// against it under <paramref name="operatorCategory"/>. Exact-match (equality/membership) string values
    /// require a <c>token</c> mapping -- never <c>string</c>, which is full-text analyzed and cannot support
    /// exact matching -- while range comparisons require an orderable <c>number</c>/<c>date</c> (or their facet
    /// equivalents) matching the value's own category.
    /// </summary>
    private static bool IsFilterValueCategoryCompatible(
        BsonDocument fieldMapping,
        FilterOperatorCategory operatorCategory,
        FilterValueCategory valueCategory)
    {
        string type = fieldMapping.GetValue("type", "").AsString;
        return operatorCategory switch
        {
            FilterOperatorCategory.Range => valueCategory switch
            {
                FilterValueCategory.Number => type is "number" or "numberFacet",
                FilterValueCategory.Date => type is "date" or "dateFacet",
                _ => false,
            },
            _ => valueCategory switch
            {
                FilterValueCategory.String => type is "token",
                FilterValueCategory.Boolean => type is "boolean",
                FilterValueCategory.Number => type is "number",
                FilterValueCategory.Date => type is "date",
                FilterValueCategory.ObjectId => type is "objectId",
                FilterValueCategory.Uuid => type is "uuid",
                _ => false,
            },
        };
    }
}

/// <summary>
/// The result of <see cref="SearchIndexEquivalence.Compare"/>: the underlying <see cref="MongoDBIndexComparison"/>
/// plus whether a dynamic mapping left any mandatory-filter field unverifiable.
/// </summary>
/// <param name="Comparison">The semantic comparison result.</param>
/// <param name="DynamicMappingFieldsUnverified">
/// <see langword="true"/> when the index uses a dynamic mapping and <see cref="MongoDBSearchIndexDefinition.MandatoryFilter"/>
/// references at least one field: <c>listSearchIndexes</c> provides no per-field enumeration for a dynamic
/// mapping, so per-field operator/value-type compatibility cannot be statically confirmed in that case. Callers
/// must not cache a result with this set to <see langword="true"/> as a fully verified success.
/// </param>
internal readonly record struct SearchIndexComparisonResult(
    MongoDBIndexComparison Comparison,
    bool DynamicMappingFieldsUnverified);
