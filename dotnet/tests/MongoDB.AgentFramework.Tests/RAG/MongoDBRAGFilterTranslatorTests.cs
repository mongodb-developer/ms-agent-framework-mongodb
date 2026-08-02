using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class MongoDBRAGFilterTranslatorTests
{
    [Fact]
    public void VectorTranslationOfNullFilterIsOmitted()
    {
        Assert.Null(RAGFilterTranslator.TranslateVectorFilter(null));
    }

    [Fact]
    public void SearchTranslationOfNullFilterIsOmitted()
    {
        Assert.Null(RAGFilterTranslator.TranslateSearchFilter(null));
    }

    [Fact]
    public void VectorTranslatesEquality()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a");

        BsonDocument? translated = RAGFilterTranslator.TranslateVectorFilter(filter);

        var expected = new BsonDocument("tenant_id", new BsonDocument("$eq", "tenant-a"));
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void VectorTranslatesInequality()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.NotEqual("status", "archived");

        BsonDocument? translated = RAGFilterTranslator.TranslateVectorFilter(filter);

        var expected = new BsonDocument("status", new BsonDocument("$ne", "archived"));
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void VectorTranslatesMembership()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.In("tenant_id", ["a", "b"]);

        BsonDocument? translated = RAGFilterTranslator.TranslateVectorFilter(filter);

        var expected = new BsonDocument(
            "tenant_id",
            new BsonDocument("$in", new BsonArray(new BsonValue[] { "a", "b" })));
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void VectorTranslatesNonMembership()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.NotIn("tenant_id", ["a", "b"]);

        BsonDocument? translated = RAGFilterTranslator.TranslateVectorFilter(filter);

        var expected = new BsonDocument(
            "tenant_id",
            new BsonDocument("$nin", new BsonArray(new BsonValue[] { "a", "b" })));
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void VectorTranslatesInclusiveRangeWithBothBounds()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Range("score", 1.0, 10.0);

        BsonDocument? translated = RAGFilterTranslator.TranslateVectorFilter(filter);

        var expected = new BsonDocument(
            "score",
            new BsonDocument { { "$gte", 1.0 }, { "$lte", 10.0 } });
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void VectorTranslatesExclusiveRangeWithSingleBound()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Range("score", 1.0, null, minimumInclusive: false);

        BsonDocument? translated = RAGFilterTranslator.TranslateVectorFilter(filter);

        var expected = new BsonDocument("score", new BsonDocument("$gt", 1.0));
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void VectorTranslatesAndAsExplicitConjunction()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
            MongoDBRAGFilter.Equal("status", "published"));

        BsonDocument? translated = RAGFilterTranslator.TranslateVectorFilter(filter);

        var expected = new BsonDocument("$and", new BsonArray(
        [
            new BsonDocument("tenant_id", new BsonDocument("$eq", "tenant-a")),
            new BsonDocument("status", new BsonDocument("$eq", "published")),
        ]));
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void VectorTranslatesOrAsExplicitDisjunction()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Or(
            MongoDBRAGFilter.Equal("status", "published"),
            MongoDBRAGFilter.Equal("status", "review"));

        BsonDocument? translated = RAGFilterTranslator.TranslateVectorFilter(filter);

        var expected = new BsonDocument("$or", new BsonArray(
        [
            new BsonDocument("status", new BsonDocument("$eq", "published")),
            new BsonDocument("status", new BsonDocument("$eq", "review")),
        ]));
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void SearchTranslatesEqualityAsEqualsOperator()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a");

        BsonArray? translated = RAGFilterTranslator.TranslateSearchFilter(filter);

        var expected = new BsonArray
        {
            new BsonDocument("equals", new BsonDocument { { "path", "tenant_id" }, { "value", "tenant-a" } }),
        };
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void SearchTranslatesInequalityAsMustNotEquals()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.NotEqual("status", "archived");

        BsonArray? translated = RAGFilterTranslator.TranslateSearchFilter(filter);

        var expected = new BsonArray
        {
            new BsonDocument(
                "compound",
                new BsonDocument(
                    "mustNot",
                    new BsonArray
                    {
                        new BsonDocument(
                            "equals",
                            new BsonDocument { { "path", "status" }, { "value", "archived" } }),
                    })),
        };
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void SearchTranslatesMembershipAsInOperator()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.In("tenant_id", ["a", "b"]);

        BsonArray? translated = RAGFilterTranslator.TranslateSearchFilter(filter);

        var expected = new BsonArray
        {
            new BsonDocument(
                "in",
                new BsonDocument
                {
                    { "path", "tenant_id" },
                    { "value", new BsonArray(new BsonValue[] { "a", "b" }) },
                }),
        };
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void SearchTranslatesRangeAsRangeOperator()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Range("score", 1.0, 10.0);

        BsonArray? translated = RAGFilterTranslator.TranslateSearchFilter(filter);

        var expected = new BsonArray
        {
            new BsonDocument(
                "range",
                new BsonDocument { { "path", "score" }, { "gte", 1.0 }, { "lte", 10.0 } }),
        };
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void SearchFlattensTopLevelAndIntoMultipleFilterClauses()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
            MongoDBRAGFilter.Equal("status", "published"));

        BsonArray? translated = RAGFilterTranslator.TranslateSearchFilter(filter);

        var expected = new BsonArray
        {
            new BsonDocument("equals", new BsonDocument { { "path", "tenant_id" }, { "value", "tenant-a" } }),
            new BsonDocument("equals", new BsonDocument { { "path", "status" }, { "value", "published" } }),
        };
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void SearchTranslatesOrAsShouldWithMinimumShouldMatch()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Or(
            MongoDBRAGFilter.Equal("status", "published"),
            MongoDBRAGFilter.Equal("status", "review"));

        BsonArray? translated = RAGFilterTranslator.TranslateSearchFilter(filter);

        var expected = new BsonArray
        {
            new BsonDocument(
                "compound",
                new BsonDocument
                {
                    {
                        "should",
                        new BsonArray
                        {
                            new BsonDocument("equals", new BsonDocument { { "path", "status" }, { "value", "published" } }),
                            new BsonDocument("equals", new BsonDocument { { "path", "status" }, { "value", "review" } }),
                        }
                    },
                    { "minimumShouldMatch", 1 },
                }),
        };
        Assert.Equal(expected, translated);
    }

    [Fact]
    public void MandatoryFilterTranslatesCompletelyIntoBothBranchesWithoutPartialLoss()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
            MongoDBRAGFilter.In("category", ["news", "docs"]),
            MongoDBRAGFilter.Range("published_at", (DateTimeOffset?)null, DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

        BsonDocument? vector = RAGFilterTranslator.TranslateVectorFilter(filter);
        BsonArray? search = RAGFilterTranslator.TranslateSearchFilter(filter);

        Assert.NotNull(vector);
        Assert.NotNull(search);
        // Every one of the three AND branches must be represented in both translations; none may be dropped.
        BsonArray vectorAnd = vector!["$and"].AsBsonArray;
        Assert.Equal(3, vectorAnd.Count);
        Assert.Equal(3, search!.Count);
    }
}
