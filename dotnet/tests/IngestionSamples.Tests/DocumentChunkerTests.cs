using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class DocumentChunkerTests
{
    [Fact]
    public void ChunkReturnsNoChunksForEmptyContent()
    {
        Assert.Empty(DocumentChunker.Chunk("", new ChunkingOptions()));
    }

    [Fact]
    public void ChunkReturnsNoChunksForWhitespaceOnlyContent()
    {
        Assert.Empty(DocumentChunker.Chunk("   \n\t  ", new ChunkingOptions()));
    }

    [Fact]
    public void ChunkReturnsOneChunkWhenContentFitsInOneWindow()
    {
        IReadOnlyList<string> chunks = DocumentChunker.Chunk(
            "short content",
            new ChunkingOptions { WindowSize = 500, OverlapSize = 50 });
        Assert.Single(chunks);
        Assert.Equal("short content", chunks[0]);
    }

    [Fact]
    public void ChunkProducesOverlappingWindowsForLongerContent()
    {
        string content = new string('a', 10) + new string('b', 10) + new string('c', 10);
        var options = new ChunkingOptions { WindowSize = 10, OverlapSize = 5 };
        IReadOnlyList<string> chunks = DocumentChunker.Chunk(content, options);

        Assert.True(chunks.Count > 1);
        // Every returned chunk must be non-empty and within the configured window bound.
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, options.WindowSize));
    }

    [Fact]
    public void ChunkNeverProducesEmptyChunks()
    {
        string content = "word " + new string(' ', 500) + "another";
        IReadOnlyList<string> chunks = DocumentChunker.Chunk(
            content,
            new ChunkingOptions { WindowSize = 20, OverlapSize = 5 });
        Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk)));
    }

    [Fact]
    public void ChunkNeverProducesDuplicateChunks()
    {
        // Repeating short content stresses both the "short tail repeats the previous window" case and a
        // non-adjacent repeated passage elsewhere in the source.
        string phrase = "The quick brown fox jumps. ";
        string content = string.Concat(Enumerable.Repeat(phrase, 5));
        IReadOnlyList<string> chunks = DocumentChunker.Chunk(
            content,
            new ChunkingOptions { WindowSize = phrase.Length, OverlapSize = 2 });

        Assert.Equal(chunks.Count, chunks.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ChunkIsDeterministicForTheSameInput()
    {
        string content = string.Concat(Enumerable.Repeat("deterministic chunking content. ", 20));
        var options = new ChunkingOptions { WindowSize = 40, OverlapSize = 10 };

        IReadOnlyList<string> first = DocumentChunker.Chunk(content, options);
        IReadOnlyList<string> second = DocumentChunker.Chunk(content, options);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ChunkCoversTheEntireTrimmedContent()
    {
        string content = string.Concat(Enumerable.Range(0, 50).Select(i => $"sentence-{i}. "));
        var options = new ChunkingOptions { WindowSize = 30, OverlapSize = 5 };
        IReadOnlyList<string> chunks = DocumentChunker.Chunk(content, options);

        // The last sentence must appear in at least one chunk -- the sliding window must reach the end of content.
        Assert.Contains(chunks, chunk => chunk.Contains("sentence-49", StringComparison.Ordinal));
    }
}
