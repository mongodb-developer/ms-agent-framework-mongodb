using MongoDB.Bson;

namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// Translates a bounded <see cref="MongoDBRAGFilter"/> into a complete Vector Search match filter or a complete
/// Search compound filter. Translation is either complete for the whole filter tree or it fails with an actionable
/// <see cref="MongoDBRetrievalException"/>; it never emits a partially translated filter.
/// </summary>
internal static class RAGFilterTranslator
{
    /// <summary>
    /// Translates <paramref name="filter"/> into a <c>$vectorSearch.filter</c> match document, or <see langword="null"/>
    /// when there is no effective filter.
    /// </summary>
    public static BsonDocument? TranslateVectorFilter(MongoDBRAGFilter? filter) =>
        filter is null ? null : TranslateVectorClause(filter);

    /// <summary>
    /// Translates <paramref name="filter"/> into a <c>$search</c> compound <c>filter</c> array, or <see langword="null"/>
    /// when there is no effective filter.
    /// </summary>
    public static BsonArray? TranslateSearchFilter(MongoDBRAGFilter? filter)
    {
        if (filter is null)
        {
            return null;
        }

        // A top-level AND flattens into multiple filter-array entries because `compound.filter` already ANDs its
        // entries; this avoids an unnecessary nested `compound` wrapper for the common mandatory-filter case.
        if (filter is MongoDBRAGFilter.LogicalFilter { Operator: MongoDBRAGFilter.LogicalOperator.And } and)
        {
            return [.. and.Operands.Select(TranslateSearchClause)];
        }

        return [TranslateSearchClause(filter)];
    }

    private static BsonDocument TranslateVectorClause(MongoDBRAGFilter filter) => filter switch
    {
        MongoDBRAGFilter.EqualityFilter equality => new BsonDocument(
            equality.FieldPath,
            new BsonDocument(equality.Negate ? "$ne" : "$eq", equality.Value)),
        MongoDBRAGFilter.MembershipFilter membership => new BsonDocument(
            membership.FieldPath,
            new BsonDocument(membership.Negate ? "$nin" : "$in", new BsonArray(membership.Values))),
        MongoDBRAGFilter.RangeFilter range => new BsonDocument(range.FieldPath, VectorRangeOperators(range)),
        MongoDBRAGFilter.LogicalFilter { Operator: MongoDBRAGFilter.LogicalOperator.And } and => new BsonDocument(
            "$and",
            new BsonArray(and.Operands.Select(TranslateVectorClause))),
        MongoDBRAGFilter.LogicalFilter { Operator: MongoDBRAGFilter.LogicalOperator.Or } or => new BsonDocument(
            "$or",
            new BsonArray(or.Operands.Select(TranslateVectorClause))),
        _ => throw new MongoDBRetrievalException(
            $"Filter node '{filter.GetType().Name}' has no Vector Search translation."),
    };

    private static BsonDocument VectorRangeOperators(MongoDBRAGFilter.RangeFilter range)
    {
        var bounds = new BsonDocument();
        if (range.Minimum is { } minimum)
        {
            bounds.Add(range.MinimumInclusive ? "$gte" : "$gt", minimum);
        }

        if (range.Maximum is { } maximum)
        {
            bounds.Add(range.MaximumInclusive ? "$lte" : "$lt", maximum);
        }

        return bounds;
    }

    private static BsonDocument TranslateSearchClause(MongoDBRAGFilter filter) => filter switch
    {
        MongoDBRAGFilter.EqualityFilter { Negate: false } equality => SearchEquals(equality),
        MongoDBRAGFilter.EqualityFilter { Negate: true } equality => MustNot(SearchEquals(equality)),
        MongoDBRAGFilter.MembershipFilter { Negate: false } membership => SearchIn(membership),
        MongoDBRAGFilter.MembershipFilter { Negate: true } membership => MustNot(SearchIn(membership)),
        MongoDBRAGFilter.RangeFilter range => SearchRange(range),
        MongoDBRAGFilter.LogicalFilter { Operator: MongoDBRAGFilter.LogicalOperator.And } and => new BsonDocument(
            "compound",
            new BsonDocument("filter", new BsonArray(and.Operands.Select(TranslateSearchClause)))),
        MongoDBRAGFilter.LogicalFilter { Operator: MongoDBRAGFilter.LogicalOperator.Or } or => new BsonDocument(
            "compound",
            new BsonDocument
            {
                { "should", new BsonArray(or.Operands.Select(TranslateSearchClause)) },
                { "minimumShouldMatch", 1 },
            }),
        _ => throw new MongoDBRetrievalException(
            $"Filter node '{filter.GetType().Name}' has no Search translation."),
    };

    private static BsonDocument SearchEquals(MongoDBRAGFilter.EqualityFilter equality) => new(
        "equals",
        new BsonDocument { { "path", equality.FieldPath }, { "value", equality.Value } });

    private static BsonDocument SearchIn(MongoDBRAGFilter.MembershipFilter membership) => new(
        "in",
        new BsonDocument { { "path", membership.FieldPath }, { "value", new BsonArray(membership.Values) } });

    private static BsonDocument SearchRange(MongoDBRAGFilter.RangeFilter range)
    {
        var document = new BsonDocument { { "path", range.FieldPath } };
        if (range.Minimum is { } minimum)
        {
            document.Add(range.MinimumInclusive ? "gte" : "gt", minimum);
        }

        if (range.Maximum is { } maximum)
        {
            document.Add(range.MaximumInclusive ? "lte" : "lt", maximum);
        }

        return new BsonDocument("range", document);
    }

    private static BsonDocument MustNot(BsonDocument clause) =>
        new("compound", new BsonDocument("mustNot", new BsonArray { clause }));
}
