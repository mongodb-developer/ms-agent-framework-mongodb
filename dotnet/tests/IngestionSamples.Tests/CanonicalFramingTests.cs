using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class CanonicalFramingTests
{
    [Fact]
    public void FrameIsDeterministicForTheSameFields()
    {
        Assert.Equal(CanonicalFraming.Frame("a", "b"), CanonicalFraming.Frame("a", "b"));
    }

    [Fact]
    public void FrameDoesNotCollideAcrossControlDelimiterFieldBoundaryShifts()
    {
        byte[] first = CanonicalFraming.Frame("a\u001fb", "c");
        byte[] second = CanonicalFraming.Frame("a", "b\u001fc");

        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public void FrameDoesNotCollideAcrossFieldCountBoundaryShifts()
    {
        // Naive concatenation of "ab"+"c" and "a"+"bc" both yield "abc"; length-prefixed framing must not collide.
        byte[] first = CanonicalFraming.Frame("ab", "c");
        byte[] second = CanonicalFraming.Frame("a", "bc");

        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public void FrameDistinguishesNullFieldFromEmptyStringField()
    {
        byte[] withNull = CanonicalFraming.Frame((string?)null, "b");
        byte[] withEmpty = CanonicalFraming.Frame("", "b");

        Assert.False(withNull.AsSpan().SequenceEqual(withEmpty));
    }

    [Fact]
    public void FrameDistinguishesDifferentFieldCounts()
    {
        byte[] twoFields = CanonicalFraming.Frame("a", "b");
        byte[] oneField = CanonicalFraming.Frame("a");

        Assert.False(twoFields.AsSpan().SequenceEqual(oneField));
    }

    [Fact]
    public void FrameRejectsNullFieldArray()
    {
        Assert.Throws<ArgumentNullException>(() => CanonicalFraming.Frame(null!));
    }
}
