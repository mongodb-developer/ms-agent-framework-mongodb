using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;

namespace MongoDB.AgentFramework;

/// <summary>
/// A bounded, closed-hierarchy typed filter AST for MongoDB RAG retrieval. Instances are created only through the
/// static factory methods, which validate field paths, value types, membership counts, operand counts, and nesting
/// depth eagerly so that every constructed filter is guaranteed to be completely translatable.
/// </summary>
public abstract class MongoDBRAGFilter
{
    /// <summary>The maximum AND/OR nesting depth accepted by any filter.</summary>
    public const int MaxNestingDepth = 6;

    /// <summary>The maximum number of values accepted by an <c>in</c>/<c>not in</c> filter.</summary>
    public const int MaxMembershipValues = 200;

    /// <summary>The maximum number of operands accepted by one AND/OR filter.</summary>
    public const int MaxLogicalOperands = 50;

    private protected MongoDBRAGFilter(int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new MongoDBConfigurationException(
                $"Filter nesting depth must not exceed {MaxNestingDepth}.");
        }

        Depth = depth;
    }

    /// <summary>Gets the nesting depth of this filter, where a leaf comparison has depth 1.</summary>
    internal int Depth { get; }

    /// <summary>Creates an equality filter.</summary>
    public static MongoDBRAGFilter Equal(string fieldPath, object value) =>
        new EqualityFilter(fieldPath, value, negate: false);

    /// <summary>Creates an inequality filter.</summary>
    public static MongoDBRAGFilter NotEqual(string fieldPath, object value) =>
        new EqualityFilter(fieldPath, value, negate: true);

    /// <summary>Creates a bounded membership filter.</summary>
    public static MongoDBRAGFilter In(string fieldPath, IEnumerable<object> values) =>
        new MembershipFilter(fieldPath, values, negate: false);

    /// <summary>Creates a bounded non-membership filter.</summary>
    public static MongoDBRAGFilter NotIn(string fieldPath, IEnumerable<object> values) =>
        new MembershipFilter(fieldPath, values, negate: true);

    /// <summary>Creates a numeric range filter. At least one bound is required.</summary>
    public static MongoDBRAGFilter Range(
        string fieldPath,
        double? minimum,
        double? maximum,
        bool minimumInclusive = true,
        bool maximumInclusive = true) =>
        new RangeFilter(
            fieldPath,
            minimum is { } min ? new BsonDouble(min) : null,
            maximum is { } max ? new BsonDouble(max) : null,
            minimumInclusive,
            maximumInclusive);

    /// <summary>Creates a date range filter. At least one bound is required.</summary>
    public static MongoDBRAGFilter Range(
        string fieldPath,
        DateTimeOffset? minimum,
        DateTimeOffset? maximum,
        bool minimumInclusive = true,
        bool maximumInclusive = true) =>
        new RangeFilter(
            fieldPath,
            minimum is { } min ? new BsonDateTime(min.UtcDateTime) : null,
            maximum is { } max ? new BsonDateTime(max.UtcDateTime) : null,
            minimumInclusive,
            maximumInclusive);

    /// <summary>Creates a bounded conjunction of at least two operands.</summary>
    public static MongoDBRAGFilter And(params MongoDBRAGFilter[] operands) =>
        new LogicalFilter(LogicalOperator.And, operands);

    /// <summary>Creates a bounded disjunction of at least two operands.</summary>
    public static MongoDBRAGFilter Or(params MongoDBRAGFilter[] operands) =>
        new LogicalFilter(LogicalOperator.Or, operands);

    internal enum LogicalOperator
    {
        And,
        Or,
    }

    internal sealed class EqualityFilter : MongoDBRAGFilter
    {
        internal EqualityFilter(string fieldPath, object value, bool negate)
            : base(1)
        {
            FieldPath = Internal.FieldPath.Validate(fieldPath, nameof(fieldPath));
            Value = RAGFilterValues.ToBsonValue(value, nameof(value));
            Negate = negate;
        }

        internal string FieldPath { get; }

        internal BsonValue Value { get; }

        internal bool Negate { get; }
    }

    internal sealed class MembershipFilter : MongoDBRAGFilter
    {
        internal MembershipFilter(string fieldPath, IEnumerable<object> values, bool negate)
            : base(1)
        {
            FieldPath = Internal.FieldPath.Validate(fieldPath, nameof(fieldPath));
            ArgumentNullException.ThrowIfNull(values);
            BsonValue[] materialized = [.. values.Select(value => RAGFilterValues.ToBsonValue(value, nameof(values)))];
            if (materialized.Length == 0)
            {
                throw new MongoDBConfigurationException("values must contain at least one entry.");
            }

            if (materialized.Length > MaxMembershipValues)
            {
                throw new MongoDBConfigurationException(
                    $"values must not exceed {MaxMembershipValues} entries.");
            }

            Values = materialized;
            Negate = negate;
        }

        internal string FieldPath { get; }

        internal IReadOnlyList<BsonValue> Values { get; }

        internal bool Negate { get; }
    }

    internal sealed class RangeFilter : MongoDBRAGFilter
    {
        internal RangeFilter(
            string fieldPath,
            BsonValue? minimum,
            BsonValue? maximum,
            bool minimumInclusive,
            bool maximumInclusive)
            : base(1)
        {
            FieldPath = Internal.FieldPath.Validate(fieldPath, nameof(fieldPath));
            if (minimum is null && maximum is null)
            {
                throw new MongoDBConfigurationException(
                    "A range filter requires a minimum, a maximum, or both.");
            }

            Minimum = minimum;
            Maximum = maximum;
            MinimumInclusive = minimumInclusive;
            MaximumInclusive = maximumInclusive;
        }

        internal string FieldPath { get; }

        internal BsonValue? Minimum { get; }

        internal BsonValue? Maximum { get; }

        internal bool MinimumInclusive { get; }

        internal bool MaximumInclusive { get; }
    }

    internal sealed class LogicalFilter : MongoDBRAGFilter
    {
        // Chains into the array-accepting constructor so the defensive copy made by CopyAndValidate is the same
        // array used both to compute the base-class depth and to populate Operands. Without this indirection, a
        // caller-owned array (e.g. passed directly to And/Or instead of via the params expansion) could be mutated
        // after construction to silently change an already-validated mandatory authorization filter.
        internal LogicalFilter(LogicalOperator @operator, IReadOnlyList<MongoDBRAGFilter> operands)
            : this(@operator, CopyAndValidate(operands))
        {
        }

        private LogicalFilter(LogicalOperator @operator, MongoDBRAGFilter[] copiedOperands)
            : base(1 + copiedOperands.Max(static operand => operand.Depth))
        {
            Operator = @operator;
            Operands = copiedOperands;
        }

        internal LogicalOperator Operator { get; }

        internal IReadOnlyList<MongoDBRAGFilter> Operands { get; }

        private static MongoDBRAGFilter[] CopyAndValidate(IReadOnlyList<MongoDBRAGFilter> operands)
        {
            ArgumentNullException.ThrowIfNull(operands);
            MongoDBRAGFilter[] copy = [.. operands];
            if (copy.Any(static operand => operand is null))
            {
                throw new ArgumentException("Operands must not contain a null filter.", nameof(operands));
            }

            if (copy.Length < 2)
            {
                throw new MongoDBConfigurationException("A logical filter requires at least two operands.");
            }

            if (copy.Length > MaxLogicalOperands)
            {
                throw new MongoDBConfigurationException(
                    $"A logical filter must not exceed {MaxLogicalOperands} operands.");
            }

            return copy;
        }
    }
}
