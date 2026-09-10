namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class MongoDBSearchModeTests
{
    [Fact]
    public void EnumDeclaresEveryRequiredRetrievalCapability()
    {
        var modes = Enum.GetValues<MongoDBSearchMode>();

        Assert.Equal(4, modes.Length);
        Assert.Contains(MongoDBSearchMode.VectorAnn, modes);
        Assert.Contains(MongoDBSearchMode.VectorEnn, modes);
        Assert.Contains(MongoDBSearchMode.FullText, modes);
        Assert.Contains(MongoDBSearchMode.HybridRrf, modes);
    }
}
