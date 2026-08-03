namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// The comparison category a leaf <see cref="MongoDBRAGFilter"/> node uses, needed (independently of the field
/// path) to check operator/mapping compatibility against a Search index's static field-mapping definitions.
/// </summary>
internal enum FilterOperatorCategory
{
    /// <summary>An equality or inequality comparison (<see cref="MongoDBRAGFilter.Equal"/>/<see cref="MongoDBRAGFilter.NotEqual"/>).</summary>
    Equality,

    /// <summary>A membership or non-membership comparison (<see cref="MongoDBRAGFilter.In"/>/<see cref="MongoDBRAGFilter.NotIn"/>).</summary>
    Membership,

    /// <summary>A numeric or date range comparison (<see cref="MongoDBRAGFilter.Range(string, double?, double?, bool, bool)"/>).</summary>
    Range,
}

/// <summary>A single field path and the operator category a mandatory filter uses against it.</summary>
internal readonly record struct FilterFieldReference(string FieldPath, FilterOperatorCategory Category);

/// <summary>
/// Extracts an immutable, de-duplicated list of the field paths and operator categories a
/// <see cref="MongoDBRAGFilter"/> tree references, used by Hybrid's capability validation to check that every
/// mandatory-filter field is actually configured (as a Vector Search <c>filter</c> field, and as an
/// operator-compatible Search mapping) rather than only translatable.
/// </summary>
internal static class RAGFilterFieldReferences
{
    public static IReadOnlyList<FilterFieldReference> Enumerate(MongoDBRAGFilter? filter)
    {
        if (filter is null)
        {
            return [];
        }

        var references = new List<FilterFieldReference>();
        Collect(filter, references);
        return [.. references.Distinct()];
    }

    private static void Collect(MongoDBRAGFilter filter, List<FilterFieldReference> references)
    {
        switch (filter)
        {
            case MongoDBRAGFilter.EqualityFilter equality:
                references.Add(new FilterFieldReference(equality.FieldPath, FilterOperatorCategory.Equality));
                break;
            case MongoDBRAGFilter.MembershipFilter membership:
                references.Add(new FilterFieldReference(membership.FieldPath, FilterOperatorCategory.Membership));
                break;
            case MongoDBRAGFilter.RangeFilter range:
                references.Add(new FilterFieldReference(range.FieldPath, FilterOperatorCategory.Range));
                break;
            case MongoDBRAGFilter.LogicalFilter logical:
                foreach (MongoDBRAGFilter operand in logical.Operands)
                {
                    Collect(operand, references);
                }

                break;
        }
    }
}
