using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class BoundedTextTruncationTests
{
    [Fact]
    public void TruncateReturnsTextUnchangedWhenWithinBound()
    {
        Assert.Equal("hello", BoundedTextTruncation.Truncate("hello", maxCharacters: 10));
    }

    [Fact]
    public void TruncateReturnsTextUnchangedWhenExactlyAtBound()
    {
        Assert.Equal("hello", BoundedTextTruncation.Truncate("hello", maxCharacters: 5));
    }

    [Fact]
    public void TruncateShortensOversizedText()
    {
        Assert.Equal("hello", BoundedTextTruncation.Truncate("hello world", maxCharacters: 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TruncateReturnsEmptyForNonPositiveBound(int maxCharacters)
    {
        Assert.Equal(string.Empty, BoundedTextTruncation.Truncate("hello", maxCharacters));
    }

    [Fact]
    public void TruncateRejectsNullText()
    {
        Assert.Throws<ArgumentNullException>(() => BoundedTextTruncation.Truncate(null!, maxCharacters: 5));
    }

    [Fact]
    public void TruncateNeverSplitsATrailingSurrogatePair()
    {
        // U+1F600 (grinning face emoji) is encoded in UTF-16 as a two-char surrogate pair. A naive cut at
        // maxCharacters=6 would land exactly between the high and low surrogate, producing an invalid orphaned
        // high surrogate at the end of the string.
        string text = "hello" + char.ConvertFromUtf32(0x1F600) + "!";
        Assert.Equal(8, text.Length);

        string truncated = BoundedTextTruncation.Truncate(text, maxCharacters: 6);

        Assert.Equal("hello", truncated);
        Assert.False(char.IsHighSurrogate(truncated[^1]));
    }

    [Fact]
    public void TruncateKeepsAnIntactSurrogatePairWhenTheBoundLandsRightAfterIt()
    {
        string text = "hello" + char.ConvertFromUtf32(0x1F600) + "!";

        string truncated = BoundedTextTruncation.Truncate(text, maxCharacters: 7);

        Assert.Equal("hello" + char.ConvertFromUtf32(0x1F600), truncated);
    }
}
