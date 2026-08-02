using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class MongoDBRAGFilterTests
{
    [Fact]
    public void EqualRejectsEmptyFieldPath()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.Equal(string.Empty, "value"));
    }

    [Fact]
    public void EqualRejectsFieldPathStartingWithDollar()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.Equal("$tenant_id", "value"));
    }

    [Fact]
    public void EqualRejectsReservedScoreAlias()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.Equal("_ragScore", "value"));
    }

    [Theory]
    [InlineData(null)]
    public void EqualRejectsNullValue(object? value)
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.Equal("tenant_id", value!));
    }

    [Fact]
    public void EqualRejectsUnsupportedValueType()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.Equal("tenant_id", new object()));
    }

    [Fact]
    public void EqualAcceptsEachSupportedScalarType()
    {
        MongoDBRAGFilter.Equal("tenant_id", "tenant-a");
        MongoDBRAGFilter.Equal("active", true);
        MongoDBRAGFilter.Equal("count", 1);
        MongoDBRAGFilter.Equal("count64", 1L);
        MongoDBRAGFilter.Equal("score", 1.5d);
        MongoDBRAGFilter.Equal("amount", 1.5m);
        MongoDBRAGFilter.Equal("created", DateTime.UtcNow);
        MongoDBRAGFilter.Equal("createdOffset", DateTimeOffset.UtcNow);
        MongoDBRAGFilter.Equal("docId", ObjectId.GenerateNewId());
    }

    [Fact]
    public void InRejectsEmptyValueList()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.In("tenant_id", []));
    }

    [Fact]
    public void InRejectsTooManyValues()
    {
        object[] values = [.. Enumerable.Range(0, MongoDBRAGFilter.MaxMembershipValues + 1).Select(static i => (object)i)];

        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.In("tenant_id", values));
    }

    [Fact]
    public void InAcceptsBoundedValueList()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.In("tenant_id", ["a", "b", "c"]);

        Assert.NotNull(filter);
    }

    [Fact]
    public void NotInAcceptsBoundedValueList()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.NotIn("tenant_id", ["a", "b"]);

        Assert.NotNull(filter);
    }

    [Fact]
    public void NumericRangeRequiresAtLeastOneBound()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.Range("score", (double?)null, (double?)null));
    }

    [Fact]
    public void NumericRangeAcceptsOneOrBothBounds()
    {
        MongoDBRAGFilter.Range("score", 1.0, 10.0);
        MongoDBRAGFilter.Range("score", 1.0, (double?)null);
        MongoDBRAGFilter.Range("score", (double?)null, 10.0);
    }

    [Fact]
    public void DateRangeRequiresAtLeastOneBound()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.Range("created", (DateTimeOffset?)null, (DateTimeOffset?)null));
    }

    [Fact]
    public void DateRangeAcceptsOneOrBothBounds()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MongoDBRAGFilter.Range("created", now.AddDays(-7), now);
    }

    [Fact]
    public void AndRequiresAtLeastTwoOperands()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.And(MongoDBRAGFilter.Equal("tenant_id", "a")));
    }

    [Fact]
    public void OrRequiresAtLeastTwoOperands()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.Or(MongoDBRAGFilter.Equal("tenant_id", "a")));
    }

    [Fact]
    public void AndRejectsTooManyOperands()
    {
        MongoDBRAGFilter[] operands = [.. Enumerable
            .Range(0, MongoDBRAGFilter.MaxLogicalOperands + 1)
            .Select(static i => MongoDBRAGFilter.Equal($"field{i}", i))];

        Assert.Throws<MongoDBConfigurationException>(
            () => MongoDBRAGFilter.And(operands));
    }

    [Fact]
    public void LogicalNestingRejectsExcessiveDepth()
    {
        MongoDBRAGFilter current = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("a", 1),
            MongoDBRAGFilter.Equal("b", 2));

        // Depth starts at 1 for the leaf-combining filter above; keep wrapping until the bound is exceeded.
        Assert.Throws<MongoDBConfigurationException>(() =>
        {
            for (int depth = 0; depth < MongoDBRAGFilter.MaxNestingDepth + 2; depth++)
            {
                current = MongoDBRAGFilter.And(current, MongoDBRAGFilter.Equal($"guard{depth}", depth));
            }
        });
    }

    [Fact]
    public void AndWithinBoundAcceptsNesting()
    {
        MongoDBRAGFilter current = MongoDBRAGFilter.Equal("a", 1);
        for (int depth = 0; depth < MongoDBRAGFilter.MaxNestingDepth - 1; depth++)
        {
            current = MongoDBRAGFilter.And(current, MongoDBRAGFilter.Equal($"field{depth}", depth));
        }

        Assert.NotNull(current);
    }
}
