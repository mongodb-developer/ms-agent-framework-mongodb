using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class ContentHashTests
{
    [Fact]
    public void ComputeIsStableForTheSameContent()
    {
        Assert.Equal(ContentHash.Compute("hello world"), ContentHash.Compute("hello world"));
    }

    [Fact]
    public void ComputeDiffersForDifferentContent()
    {
        Assert.NotEqual(ContentHash.Compute("hello world"), ContentHash.Compute("goodbye world"));
    }

    [Fact]
    public void ComputeRejectsNullContent()
    {
        Assert.Throws<ArgumentNullException>(() => ContentHash.Compute(null!));
    }

    [Fact]
    public void ComputeFramedIsStableForTheSameFields()
    {
        Assert.Equal(
            ContentHash.ComputeFramed("title", "https://example.test", "body"),
            ContentHash.ComputeFramed("title", "https://example.test", "body"));
    }

    [Fact]
    public void ComputeFramedDoesNotCollideAcrossControlDelimiterFieldBoundaryShifts()
    {
        // A delimiter-joined preimage (e.g. string.Join('\u001f', title, url, content)) would hash
        // ("a\u001fb", "c", "same-body") and ("a", "b\u001fc", "same-body") identically, since both concatenate to
        // "a\u001fb\u001fc\u001fsame-body". Canonical length-prefixed framing must keep them distinct.
        string first = ContentHash.ComputeFramed("a\u001fb", "c", "same-body");
        string second = ContentHash.ComputeFramed("a", "b\u001fc", "same-body");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeFramedDistinguishesNullFieldFromEmptyStringField()
    {
        string withNull = ContentHash.ComputeFramed((string?)null, "b");
        string withEmpty = ContentHash.ComputeFramed("", "b");

        Assert.NotEqual(withNull, withEmpty);
    }

    [Fact]
    public void ComputeFramedDoesNotCollideAcrossFieldCountBoundaryShifts()
    {
        // Concatenating "ab" + "c" and "a" + "bc" produce the same raw bytes without framing; length-prefixed
        // framing must still keep them distinct.
        string first = ContentHash.ComputeFramed("ab", "c");
        string second = ContentHash.ComputeFramed("a", "bc");

        Assert.NotEqual(first, second);
    }
}
