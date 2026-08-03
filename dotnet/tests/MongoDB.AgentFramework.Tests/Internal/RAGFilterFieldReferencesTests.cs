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
        Assert.Contains(new FilterFieldReference("tenant_id", FilterOperatorCategory.Equality), references);
        Assert.Contains(new FilterFieldReference("category", FilterOperatorCategory.Membership), references);
        Assert.Contains(new FilterFieldReference("published_at", FilterOperatorCategory.Range), references);
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
}
