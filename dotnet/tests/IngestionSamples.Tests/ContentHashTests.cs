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
}
