using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class ParentDocumentRetrieverTests
{
    [Fact]
    public async Task SearchAsyncReturnsHydratedParentsOrderedByBestChildScore()
    {
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResult("child-1", score: 0.9, parentId: "parent-a"),
            FakeChildChunkSearcher.ChildResult("child-2", score: 0.5, parentId: "parent-b"),
        ]);
        var lookup = new FakeParentLookup(
        [
            ("tenant-a", new ParentDocument("parent-a", "Parent A content", "Source A", null)),
            ("tenant-a", new ParentDocument("parent-b", "Parent B content", "Source B", null)),
        ]);
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a");

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Equal(2, results.Count);
        Assert.Equal("parent-a", results[0].ParentId);
        Assert.Equal("Source A", results[0].SourceName);
        Assert.Equal("parent-b", results[1].ParentId);
    }

    [Fact]
    public async Task SearchAsyncDeDuplicatesMultipleChildrenSharingTheSameParent()
    {
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResult("child-1", score: 0.9, parentId: "parent-a"),
            FakeChildChunkSearcher.ChildResult("child-2", score: 0.8, parentId: "parent-a"),
        ]);
        var lookup = new FakeParentLookup([("tenant-a", new ParentDocument("parent-a", "Parent A content", null, null))]);
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a");

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Single(results);
        // The best (first-ranked) child match must be the one attached to the de-duplicated parent result.
        Assert.Equal("child-1", results[0].BestChildId);
    }

    [Fact]
    public async Task SearchAsyncBoundsFanOutToMaxParentsBeforeIssuingTheLookup()
    {
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResult("child-1", score: 0.9, parentId: "parent-a"),
            FakeChildChunkSearcher.ChildResult("child-2", score: 0.8, parentId: "parent-b"),
            FakeChildChunkSearcher.ChildResult("child-3", score: 0.7, parentId: "parent-c"),
        ]);
        var lookup = new FakeParentLookup(
        [
            ("tenant-a", new ParentDocument("parent-a", "A", null, null)),
            ("tenant-a", new ParentDocument("parent-b", "B", null, null)),
            ("tenant-a", new ParentDocument("parent-c", "C", null, null)),
        ]);
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a", maxParents: 2);

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Equal(2, results.Count);
        Assert.Equal(2, lookup.LastRequestedParentIds!.Count);
    }

    [Fact]
    public async Task SearchAsyncSkipsChildResultsMissingParentLinkage()
    {
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResultWithoutParent("child-orphan", score: 0.9),
            FakeChildChunkSearcher.ChildResult("child-2", score: 0.5, parentId: "parent-a"),
        ]);
        var lookup = new FakeParentLookup([("tenant-a", new ParentDocument("parent-a", "A", null, null))]);
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a");

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Single(results);
        Assert.Equal("parent-a", results[0].ParentId);
    }

    [Fact]
    public async Task SearchAsyncOmitsParentsAbsentFromTheAuthorizedLookupResult()
    {
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResult("child-1", score: 0.9, parentId: "parent-deleted"),
        ]);
        var lookup = new FakeParentLookup([]);
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a");

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsyncNeverLooksUpParentsFromAnotherTenant()
    {
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResult("child-1", score: 0.9, parentId: "parent-a"),
        ]);
        var lookup = new FakeParentLookup([("tenant-b", new ParentDocument("parent-a", "Tenant B's content", null, null))]);
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a");

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsyncReturnsNoResultsWithoutCallingTheLookupWhenNoChildrenMatch()
    {
        var searcher = new FakeChildChunkSearcher([]);
        var lookup = new FakeParentLookup([]);
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a");

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Empty(results);
        Assert.Null(lookup.LastRequestedParentIds);
    }

    [Fact]
    public async Task SearchAsyncPropagatesCancellation()
    {
        var searcher = new FakeChildChunkSearcher([]);
        var lookup = new FakeParentLookup([]);
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => retriever.SearchAsync("query", cts.Token));
    }

    [Fact]
    public void ConstructorRejectsEmptyTenantId()
    {
        var searcher = new FakeChildChunkSearcher([]);
        var lookup = new FakeParentLookup([]);

        Assert.Throws<IngestionValidationException>(() => new ParentDocumentRetriever(searcher, lookup, ""));
    }

    [Fact]
    public void ConstructorRejectsNonPositiveMaxParents()
    {
        var searcher = new FakeChildChunkSearcher([]);
        var lookup = new FakeParentLookup([]);

        Assert.Throws<IngestionValidationException>(
            () => new ParentDocumentRetriever(searcher, lookup, "tenant-a", maxParents: 0));
    }

    [Fact]
    public void ConstructorRejectsInvalidContextBoundingOptions()
    {
        var searcher = new FakeChildChunkSearcher([]);
        var lookup = new FakeParentLookup([]);
        var invalidBounds = new ParentContextBoundingOptions { MaxCharactersPerParent = 0 };

        Assert.Throws<IngestionValidationException>(
            () => new ParentDocumentRetriever(searcher, lookup, "tenant-a", contextBounding: invalidBounds));
    }

    [Fact]
    public async Task SearchAsyncTruncatesAnOversizedSingleParentToThePerParentBound()
    {
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResult("child-1", score: 0.9, parentId: "parent-a"),
        ]);
        var lookup = new FakeParentLookup(
            [("tenant-a", new ParentDocument("parent-a", new string('x', 100), null, null))]);
        var bounds = new ParentContextBoundingOptions { MaxCharactersPerParent = 10, MaxTotalContextCharacters = 1000 };
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a", contextBounding: bounds);

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Single(results);
        Assert.Equal(10, results[0].Content.Length);
        Assert.Equal(new string('x', 10), results[0].Content);
    }

    [Fact]
    public async Task SearchAsyncTruncatesLaterParentsOnceTheTotalContextBudgetIsExhaustedPreservingOrder()
    {
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResult("child-1", score: 0.9, parentId: "parent-a"),
            FakeChildChunkSearcher.ChildResult("child-2", score: 0.8, parentId: "parent-b"),
            FakeChildChunkSearcher.ChildResult("child-3", score: 0.7, parentId: "parent-c"),
        ]);
        var lookup = new FakeParentLookup(
        [
            ("tenant-a", new ParentDocument("parent-a", new string('a', 10), null, null)),
            ("tenant-a", new ParentDocument("parent-b", new string('b', 10), null, null)),
            ("tenant-a", new ParentDocument("parent-c", new string('c', 10), null, null)),
        ]);
        // Per-parent bound (10) never truncates individually, but the total budget (15) only fits the first parent
        // in full (10 chars) plus 5 more characters of the second parent; the third parent's budget is exhausted.
        var bounds = new ParentContextBoundingOptions { MaxCharactersPerParent = 10, MaxTotalContextCharacters = 15 };
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a", maxParents: 3, contextBounding: bounds);

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Equal(2, results.Count);
        Assert.Equal("parent-a", results[0].ParentId);
        Assert.Equal(new string('a', 10), results[0].Content);
        Assert.Equal("parent-b", results[1].ParentId);
        Assert.Equal(new string('b', 5), results[1].Content);
    }

    [Fact]
    public async Task SearchAsyncNeverSplitsASurrogatePairWhenTruncatingParentContent()
    {
        string emoji = char.ConvertFromUtf32(0x1F600);
        var searcher = new FakeChildChunkSearcher(
        [
            FakeChildChunkSearcher.ChildResult("child-1", score: 0.9, parentId: "parent-a"),
        ]);
        var lookup = new FakeParentLookup(
            [("tenant-a", new ParentDocument("parent-a", "hello" + emoji + "!", null, null))]);
        var bounds = new ParentContextBoundingOptions { MaxCharactersPerParent = 6, MaxTotalContextCharacters = 1000 };
        var retriever = new ParentDocumentRetriever(searcher, lookup, "tenant-a", contextBounding: bounds);

        IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync("query");

        Assert.Single(results);
        Assert.Equal("hello", results[0].Content);
        Assert.False(char.IsHighSurrogate(results[0].Content[^1]));
    }
}
