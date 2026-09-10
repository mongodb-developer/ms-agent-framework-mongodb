using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework.Tests.Internal;

public sealed class RAGFilterFieldReferencesTests
{
    [Fact]
    public void Enumerate_returns_empty_for_a_null_filter()
    {
        Assert.Empty(RAGFilterFieldReferences.Enumerate(null));
    }

    [Fact]
    public void Enumerate_extracts_the_field_path_and_category_of_a_leaf_equality_filter()
    {
        FilterFieldReference reference = Assert.Single(
            RAGFilterFieldReferences.Enumerate(MongoDBRAGFilter.Equal("tenant_id", "tenant-a")));

        Assert.Equal("tenant_id", reference.FieldPath);
        Assert.Equal(FilterOperatorCategory.Equality, reference.Category);
        Assert.Equal(FilterValueCategory.String, reference.ValueCategories);
    }

    [Fact]
    public void Enumerate_categorizes_inequality_as_equality()
    {
        FilterFieldReference reference = Assert.Single(
            RAGFilterFieldReferences.Enumerate(MongoDBRAGFilter.NotEqual("tenant_id", "tenant-a")));

        Assert.Equal(FilterOperatorCategory.Equality, reference.Category);
    }

    [Fact]
    public void Enumerate_categorizes_membership_filters()
    {
        FilterFieldReference reference = Assert.Single(
            RAGFilterFieldReferences.Enumerate(MongoDBRAGFilter.In("category", ["docs", "faq"])));

        Assert.Equal("category", reference.FieldPath);
        Assert.Equal(FilterOperatorCategory.Membership, reference.Category);
        Assert.Equal(FilterValueCategory.String, reference.ValueCategories);
    }

    [Fact]
    public void Enumerate_categorizes_not_in_as_membership()
    {
        FilterFieldReference reference = Assert.Single(
            RAGFilterFieldReferences.Enumerate(MongoDBRAGFilter.NotIn("category", ["docs"])));

        Assert.Equal(FilterOperatorCategory.Membership, reference.Category);
    }

    [Fact]
    public void Enumerate_categorizes_range_filters()
    {
        FilterFieldReference reference = Assert.Single(
            RAGFilterFieldReferences.Enumerate(MongoDBRAGFilter.Range("published_at", minimum: 0, maximum: null)));

        Assert.Equal("published_at", reference.FieldPath);
        Assert.Equal(FilterOperatorCategory.Range, reference.Category);
        Assert.Equal(FilterValueCategory.Number, reference.ValueCategories);
    }

    [Fact]
    public void Enumerate_unions_heterogeneous_membership_value_categories()
    {
        FilterFieldReference reference = Assert.Single(
            RAGFilterFieldReferences.Enumerate(MongoDBRAGFilter.In("mixed_id", ["tenant-a", 42])));

        Assert.Equal(FilterValueCategory.String | FilterValueCategory.Number, reference.ValueCategories);
    }

    [Fact]
    public void Enumerate_categorizes_date_range_filters()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FilterFieldReference reference = Assert.Single(
            RAGFilterFieldReferences.Enumerate(MongoDBRAGFilter.Range("created", now.AddDays(-7), now)));

        Assert.Equal(FilterValueCategory.Date, reference.ValueCategories);
    }

    [Fact]
    public void Enumerate_recurses_through_nested_and_or_filters()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
            MongoDBRAGFilter.Or(
                MongoDBRAGFilter.In("category", ["docs", "faq"]),
                MongoDBRAGFilter.Range("published_at", minimum: 0, maximum: null)));

        IReadOnlyList<FilterFieldReference> references = RAGFilterFieldReferences.Enumerate(filter);

        Assert.Equal(3, references.Count);
        Assert.Contains(
            new FilterFieldReference("tenant_id", FilterOperatorCategory.Equality, FilterValueCategory.String),
            references);
        Assert.Contains(
            new FilterFieldReference("category", FilterOperatorCategory.Membership, FilterValueCategory.String),
            references);
        Assert.Contains(
            new FilterFieldReference("published_at", FilterOperatorCategory.Range, FilterValueCategory.Number),
            references);
    }

    [Fact]
    public void Enumerate_de_duplicates_repeated_field_and_category_combinations()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Or(
            MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
            MongoDBRAGFilter.Equal("tenant_id", "tenant-b"));

        IReadOnlyList<FilterFieldReference> references = RAGFilterFieldReferences.Enumerate(filter);

        FilterFieldReference reference = Assert.Single(references);
        Assert.Equal("tenant_id", reference.FieldPath);
    }

    [Fact]
    public void Enumerate_keeps_distinct_references_for_the_same_field_and_category_with_different_value_categories()
    {
        // Not constructible through the public MongoDBRAGFilter API for a single Equal/NotEqual call (a single
        // value can only have one BSON type), but two separate equality filters on the same field with
        // differently-typed values still legitimately reference the same field path and operator category, so
        // they should not collapse to a single reference if their value categories differ.
        MongoDBRAGFilter filter = MongoDBRAGFilter.Or(
            MongoDBRAGFilter.Equal("external_id", "abc"),
            MongoDBRAGFilter.Equal("external_id", 42));

        IReadOnlyList<FilterFieldReference> references = RAGFilterFieldReferences.Enumerate(filter);

        Assert.Equal(2, references.Count);
        Assert.Contains(
            new FilterFieldReference("external_id", FilterOperatorCategory.Equality, FilterValueCategory.String),
            references);
        Assert.Contains(
            new FilterFieldReference("external_id", FilterOperatorCategory.Equality, FilterValueCategory.Number),
            references);
    }
}
