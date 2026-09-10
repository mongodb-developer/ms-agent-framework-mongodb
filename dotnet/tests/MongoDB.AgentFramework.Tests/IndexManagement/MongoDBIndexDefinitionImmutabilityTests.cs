using System.Collections.Concurrent;

namespace MongoDB.AgentFramework.Tests.IndexManagement;

/// <summary>
/// Regression coverage proving <see cref="MongoDBVectorSearchIndexDefinition.FilterFieldPaths"/>,
/// <see cref="MongoDBSearchIndexDefinition.TextFieldNames"/>, <see cref="MongoDBIndexComparison.Mismatches"/>, and
/// <see cref="MongoDBIndexComparison.CompatibleDifferences"/> are truly immutable: the concrete instance is never
/// castable back to a mutable backing collection, every mutating <see cref="IList{T}"/> member throws, mutating the
/// caller's original source collection after construction never affects the snapshot, and concurrent reads never
/// race or throw.
/// </summary>
public sealed class MongoDBIndexDefinitionImmutabilityTests
{
    [Fact]
    public void VectorSearchDefinitionFilterFieldPathsIsNotCastableToAMutableBackingCollection()
    {
        var definition = new MongoDBVectorSearchIndexDefinition(
            "vec_index", "embedding", 1536, filterFieldPaths: ["tenant_id", "category"]);

        AssertNotCastableToMutableBackingCollection(definition.FilterFieldPaths);
    }

    [Fact]
    public void VectorSearchDefinitionFilterFieldPathsThrowsOnMutationAttempts()
    {
        var definition = new MongoDBVectorSearchIndexDefinition(
            "vec_index", "embedding", 1536, filterFieldPaths: ["tenant_id"]);

        AssertMutationThrows(definition.FilterFieldPaths);
    }

    [Fact]
    public void VectorSearchDefinitionFilterFieldPathsIsADeepSnapshotOfTheCallerSSourceList()
    {
        var source = new List<string> { "tenant_id", "category" };
        var definition = new MongoDBVectorSearchIndexDefinition(
            "vec_index", "embedding", 1536, filterFieldPaths: source);

        source.Add("mutated_after_construction");
        source[0] = "overwritten";

        Assert.Equal(["tenant_id", "category"], definition.FilterFieldPaths);
    }

    [Fact]
    public async Task VectorSearchDefinitionFilterFieldPathsSupportsConcurrentReadsWithoutRacingOrThrowing()
    {
        var definition = new MongoDBVectorSearchIndexDefinition(
            "vec_index", "embedding", 1536, filterFieldPaths: ["tenant_id", "category", "region"]);

        await AssertConcurrentReadsAreSafe(definition.FilterFieldPaths);
    }

    [Fact]
    public void SearchDefinitionTextFieldNamesIsNotCastableToAMutableBackingCollection()
    {
        var definition = new MongoDBSearchIndexDefinition("text_index", ["title", "body"]);

        AssertNotCastableToMutableBackingCollection(definition.TextFieldNames);
    }

    [Fact]
    public void SearchDefinitionTextFieldNamesThrowsOnMutationAttempts()
    {
        var definition = new MongoDBSearchIndexDefinition("text_index", ["title"]);

        AssertMutationThrows(definition.TextFieldNames);
    }

    [Fact]
    public void SearchDefinitionTextFieldNamesIsADeepSnapshotOfTheCallerSSourceArray()
    {
        string[] source = ["title", "body"];
        var definition = new MongoDBSearchIndexDefinition("text_index", source);

        source[0] = "overwritten";

        Assert.Equal(["title", "body"], definition.TextFieldNames);
    }

    [Fact]
    public async Task SearchDefinitionTextFieldNamesSupportsConcurrentReadsWithoutRacingOrThrowing()
    {
        var definition = new MongoDBSearchIndexDefinition("text_index", ["title", "body", "summary"]);

        await AssertConcurrentReadsAreSafe(definition.TextFieldNames);
    }

    [Fact]
    public void IndexComparisonMismatchesIsNotCastableToAMutableBackingCollection()
    {
        var comparison = new MongoDBIndexComparison(["vectorDimensions mismatch"]);

        AssertNotCastableToMutableBackingCollection(comparison.Mismatches);
    }

    [Fact]
    public void IndexComparisonMismatchesThrowsOnMutationAttempts()
    {
        var comparison = new MongoDBIndexComparison(["vectorDimensions mismatch"]);

        AssertMutationThrows(comparison.Mismatches);
    }

    [Fact]
    public void IndexComparisonMismatchesIsADeepSnapshotOfTheCallerSSourceListEvenThoughThereWasPreviouslyNoDefensiveCopyAtAll()
    {
        var source = new List<string> { "vectorDimensions mismatch" };
        var comparison = new MongoDBIndexComparison(source);

        source.Add("mutated_after_construction");

        Assert.Equal(["vectorDimensions mismatch"], comparison.Mismatches);
    }

    [Fact]
    public void IndexComparisonCompatibleDifferencesIsNotCastableToAMutableBackingCollection()
    {
        var comparison = new MongoDBIndexComparison([], ["extra server-default key"]);

        AssertNotCastableToMutableBackingCollection(comparison.CompatibleDifferences);
    }

    [Fact]
    public void IndexComparisonCompatibleDifferencesThrowsOnMutationAttempts()
    {
        var comparison = new MongoDBIndexComparison([], ["extra server-default key"]);

        AssertMutationThrows(comparison.CompatibleDifferences);
    }

    [Fact]
    public void IndexComparisonCompatibleDifferencesIsADeepSnapshotOfTheCallerSSourceList()
    {
        var source = new List<string> { "extra server-default key" };
        var comparison = new MongoDBIndexComparison([], source);

        source.Add("mutated_after_construction");

        Assert.Equal(["extra server-default key"], comparison.CompatibleDifferences);
    }

    [Fact]
    public async Task IndexComparisonMismatchesSupportsConcurrentReadsWithoutRacingOrThrowing()
    {
        var comparison = new MongoDBIndexComparison(["a mismatch", "another mismatch"]);

        await AssertConcurrentReadsAreSafe(comparison.Mismatches);
    }

    private static void AssertNotCastableToMutableBackingCollection(IReadOnlyList<string> snapshot)
    {
        Assert.False(snapshot is string[], "Snapshot must not be a plain, directly-mutable array.");
        Assert.False(snapshot is List<string>, "Snapshot must not be a plain, directly-mutable List<T>.");
    }

    private static void AssertMutationThrows(IReadOnlyList<string> snapshot)
    {
        var mutable = Assert.IsAssignableFrom<IList<string>>(snapshot);

        Assert.Throws<NotSupportedException>(() => mutable.Add("new"));
        Assert.Throws<NotSupportedException>(() => mutable.Clear());
        Assert.Throws<NotSupportedException>(() => mutable.RemoveAt(0));
        if (mutable.Count > 0)
        {
            Assert.Throws<NotSupportedException>(() => mutable[0] = "overwritten");
        }
    }

    private static async Task AssertConcurrentReadsAreSafe(IReadOnlyList<string> snapshot)
    {
        var exceptions = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    _ = snapshot.Count;
                    foreach (string _2 in snapshot)
                    {
                        // Force full enumeration under concurrency.
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
    }
}
