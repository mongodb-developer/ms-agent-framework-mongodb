using MongoDB.AgentFramework.Internal.IndexManagement;
using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.Internal.IndexManagement;

/// <summary>
/// Build-&gt;validate roundtrip tests for <see cref="SearchIndexEquivalence.BuildDefinition"/>: proves the emitted
/// mapping document is itself accepted by <see cref="SearchIndexEquivalence.Validate"/>, and asserts the exact
/// nested/merged/multi-category shapes required by docs/spec/features/index-management.md.
/// </summary>
public sealed class SearchIndexEquivalenceTests
{
    [Fact]
    public void BuildDefinition_emits_nested_document_fields_for_a_dotted_path_not_a_literal_dotted_key()
    {
        var definition = new MongoDBSearchIndexDefinition(
            "facade_search",
            ["content"],
            MongoDBRAGFilter.Equal("metadata.tenant_id", "acme"));

        BsonDocument built = SearchIndexEquivalence.BuildDefinition(definition);

        BsonDocument fields = built["mappings"]["fields"].AsBsonDocument;
        Assert.False(fields.Contains("metadata.tenant_id"));
        BsonDocument metadata = Assert.IsType<BsonDocument>(fields["metadata"]);
        Assert.Equal("document", metadata["type"].AsString);
        BsonDocument nestedFields = metadata["fields"].AsBsonDocument;
        Assert.Equal("token", nestedFields["tenant_id"]["type"].AsString);

        RoundtripValidate(built, definition);
    }

    [Fact]
    public void BuildDefinition_merges_text_and_filter_requirements_on_the_same_path()
    {
        // "title" is both a configured text field and the target of a string-equality mandatory filter: it must
        // satisfy both a text query ("string") and an exact-match filter ("token") simultaneously.
        var definition = new MongoDBSearchIndexDefinition(
            "facade_search",
            ["title"],
            MongoDBRAGFilter.Equal("title", "acme"));

        BsonDocument built = SearchIndexEquivalence.BuildDefinition(definition);

        BsonDocument fields = built["mappings"]["fields"].AsBsonDocument;
        BsonArray mapping = Assert.IsType<BsonArray>(fields["title"]);
        var types = mapping.Select(static m => m["type"].AsString).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "string", "token" }, types);

        RoundtripValidate(built, definition);
    }

    [Fact]
    public void BuildDefinition_emits_a_mapping_array_satisfying_every_heterogeneous_filter_value_category()
    {
        // A single membership filter over mixed string/number values requires both "token" and "number" mapped
        // simultaneously at the same path (Atlas Search's multi-type mapping array).
        var definition = new MongoDBSearchIndexDefinition(
            "facade_search",
            ["content"],
            MongoDBRAGFilter.In("mixed_id", ["acme", 42]));

        BsonDocument built = SearchIndexEquivalence.BuildDefinition(definition);

        BsonDocument fields = built["mappings"]["fields"].AsBsonDocument;
        BsonArray mapping = Assert.IsType<BsonArray>(fields["mixed_id"]);
        var types = mapping.Select(static m => m["type"].AsString).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "token", "number" }, types);

        RoundtripValidate(built, definition);
    }

    [Fact]
    public void BuildDefinition_roundtrips_a_nested_path_combined_with_a_range_filter_on_a_sibling_field()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("metadata.tenant_id", "acme"),
            MongoDBRAGFilter.Range("metadata.published_at", minimum: DateTimeOffset.UnixEpoch, maximum: null));
        var definition = new MongoDBSearchIndexDefinition("facade_search", ["content"], filter);

        BsonDocument built = SearchIndexEquivalence.BuildDefinition(definition);

        BsonDocument metadataFields = built["mappings"]["fields"]["metadata"]["fields"].AsBsonDocument;
        Assert.Equal("token", metadataFields["tenant_id"]["type"].AsString);
        Assert.Equal("date", metadataFields["published_at"]["type"].AsString);

        RoundtripValidate(built, definition);
    }

    [Theory]
    [InlineData("string", true)]
    [InlineData("token", false)]
    [InlineData("autocomplete", false)]
    [InlineData("number", false)]
    public void Compare_treats_only_a_string_mapping_as_text_compatible(string mappedType, bool expectCompatible)
    {
        var definition = new MongoDBSearchIndexDefinition("facade_search", ["content"]);
        var indexDefinition = new BsonDocument(
            "mappings",
            new BsonDocument
            {
                { "dynamic", false },
                { "fields", new BsonDocument("content", new BsonDocument("type", mappedType)) },
            });

        SearchIndexComparisonResult result = SearchIndexEquivalence.Compare(indexDefinition, definition);

        Assert.Equal(expectCompatible, result.Comparison.IsCompatible);
        if (!expectCompatible)
        {
            Assert.Contains(result.Comparison.Mismatches, m => m.Contains("text-searchable", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Wraps <paramref name="built"/> as a fake READY/queryable index document and validates it.</summary>
    private static void RoundtripValidate(BsonDocument built, MongoDBSearchIndexDefinition definition)
    {
        var index = new BsonDocument
        {
            { "name", definition.IndexName },
            { "type", "search" },
            { "status", "READY" },
            { "queryable", true },
            { "latestDefinition", built },
        };

        SearchIndexComparisonResult result = SearchIndexEquivalence.Validate(index, definition, requireReady: true);
        Assert.True(result.Comparison.IsCompatible);
        Assert.Empty(result.Comparison.Mismatches);
    }
}
