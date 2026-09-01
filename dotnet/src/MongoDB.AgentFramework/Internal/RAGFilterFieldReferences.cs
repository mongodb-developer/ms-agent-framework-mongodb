using MongoDB.Bson;

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

/// <summary>
/// The BSON value category of one or more leaf-filter values, needed to check compatibility against a Search
/// index's per-field mapping <c>type</c> (which is value-type-specific: for example a string equality value
/// requires a <c>token</c>-mapped field, never a <c>string</c>-mapped one, since <c>string</c> fields are
/// full-text analyzed and are not exact-match compatible). This is a <see cref="FlagsAttribute"/> enum because a
/// membership (<c>in</c>/<c>not in</c>) filter may reference heterogeneous value types across its value list, in
/// which case every referenced category must independently have a compatible mapping.
/// </summary>
[Flags]
internal enum FilterValueCategory
{
    /// <summary>No value category (never produced for an actual filter value; used only as the empty flag state).</summary>
    None = 0,

    /// <summary>A string value, compatible only with a Search <c>token</c> mapping (not <c>string</c>).</summary>
    String = 1 << 0,

    /// <summary>A boolean value, compatible with a Search <c>boolean</c> mapping.</summary>
    Boolean = 1 << 1,

    /// <summary>A numeric value (32/64-bit integer, double, or decimal), compatible with a Search <c>number</c> mapping.</summary>
    Number = 1 << 2,

    /// <summary>A date/time value, compatible with a Search <c>date</c> mapping.</summary>
    Date = 1 << 3,

    /// <summary>An <see cref="ObjectId"/> value, compatible with a Search <c>objectId</c> mapping.</summary>
    ObjectId = 1 << 4,

    /// <summary>
    /// A UUID (binary subtype 4) value, compatible with a Search <c>uuid</c> mapping. Not reachable through any
    /// public <see cref="MongoDBRAGFilter"/> factory today (see <see cref="Internal.RAGFilterValues.ToBsonValue"/>,
    /// which does not accept <see cref="Guid"/>); retained so the category set is complete and forward-compatible
    /// if UUID filter values are ever added, without requiring another breaking enum change.
    /// </summary>
    Uuid = 1 << 5,
}

/// <summary>Computes the <see cref="FilterValueCategory"/> of a concrete <see cref="BsonValue"/>.</summary>
internal static class BsonValueCategories
{
    /// <summary>
    /// Categorizes <paramref name="value"/>. Every BSON type <see cref="RAGFilterValues.ToBsonValue"/> can ever
    /// produce is covered; any other type (unreachable through the public <see cref="MongoDBRAGFilter"/> API
    /// today, but defended against for any future internal construction path) throws
    /// <see cref="MongoDBConfigurationException"/> rather than silently miscategorizing it.
    /// </summary>
    public static FilterValueCategory Of(BsonValue value) => value switch
    {
        BsonString => FilterValueCategory.String,
        BsonBoolean => FilterValueCategory.Boolean,
        BsonInt32 or BsonInt64 or BsonDouble or BsonDecimal128 => FilterValueCategory.Number,
        BsonDateTime => FilterValueCategory.Date,
        BsonObjectId => FilterValueCategory.ObjectId,
        BsonBinaryData binary when binary.SubType is BsonBinarySubType.UuidStandard or BsonBinarySubType.UuidLegacy =>
            FilterValueCategory.Uuid,
        _ => throw new MongoDBConfigurationException(
            $"Filter values of BSON type '{value.BsonType}' are not supported."),
    };

    /// <summary>Enumerates each individual flag set in <paramref name="categories"/>.</summary>
    public static IEnumerable<FilterValueCategory> Flags(FilterValueCategory categories)
    {
        foreach (FilterValueCategory flag in Enum.GetValues<FilterValueCategory>())
        {
            if (flag != FilterValueCategory.None && categories.HasFlag(flag))
            {
                yield return flag;
            }
        }
    }
}

/// <summary>
/// A single field path, the operator category a mandatory filter uses against it, and the BSON value
/// category/categories of the value(s) compared against it.
/// </summary>
internal readonly record struct FilterFieldReference(
    string FieldPath,
    FilterOperatorCategory Category,
    FilterValueCategory ValueCategories);

/// <summary>
/// Extracts an immutable, de-duplicated list of the field paths, operator categories, and value categories a
/// <see cref="MongoDBRAGFilter"/> tree references, used by Hybrid's capability validation to check that every
/// mandatory-filter field is actually configured (as a Vector Search <c>filter</c> field, and as an
/// operator-and-value-type-compatible Search mapping) rather than only translatable.
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
                references.Add(new FilterFieldReference(
                    equality.FieldPath, FilterOperatorCategory.Equality, BsonValueCategories.Of(equality.Value)));
                break;
            case MongoDBRAGFilter.MembershipFilter membership:
                FilterValueCategory membershipCategories = membership.Values
                    .Select(BsonValueCategories.Of)
                    .Aggregate(FilterValueCategory.None, (accumulated, category) => accumulated | category);
                references.Add(new FilterFieldReference(
                    membership.FieldPath, FilterOperatorCategory.Membership, membershipCategories));
                break;
            case MongoDBRAGFilter.RangeFilter range:
                // RangeFilter's constructor guarantees Minimum and Maximum share the same value category when
                // both are present, so whichever bound exists (at least one is guaranteed) determines the
                // category.
                references.Add(new FilterFieldReference(
                    range.FieldPath,
                    FilterOperatorCategory.Range,
                    BsonValueCategories.Of(range.Minimum ?? range.Maximum!)));
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
