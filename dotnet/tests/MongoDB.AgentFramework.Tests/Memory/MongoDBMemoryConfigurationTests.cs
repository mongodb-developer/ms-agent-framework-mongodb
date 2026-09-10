using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Driver;
using System.Reflection;

namespace MongoDB.AgentFramework.Tests.Memory;

public sealed class MongoDBMemoryConfigurationTests
{
    [Theory]
    [InlineData("content")]
    [InlineData("content.embedding")]
    [InlineData("user_id.vector")]
    public void OptionsRejectVectorPathThatOverlapsCanonicalField(string path)
    {
        var options = new MongoDBMemoryProviderOptions { VectorFieldName = path };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void ScopeRequiresDurableIdentity()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => new MongoDBMemoryScope());
        Assert.Throws<MongoDBConfigurationException>(
            () => new MongoDBMemoryScope(userId: " "));
    }

    [Fact]
    public void OptionsRejectInvalidAnnCandidateCount()
    {
        var options = new MongoDBMemoryProviderOptions
        {
            MaxResults = 10,
            NumCandidates = 9,
        };

        Assert.Throws<MongoDBConfigurationException>(() => options.Validate());
    }

    [Fact]
    public void ConstructorUsesPublicContextContractWithoutContactingMongoDB()
    {
        var collection = DispatchProxy.Create<IMongoCollection<MongoDB.Bson.BsonDocument>, ThrowingProxy>();
        var provider = new MongoDBMemoryProvider(
            collection,
            new FakeEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope(userId: "user-a")));

        Assert.IsAssignableFrom<AIContextProvider>(provider);
    }

    private sealed class FakeEmbeddingGenerator :
        IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Construction generated embeddings.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(
            System.Reflection.MethodInfo? targetMethod,
            object?[]? args) =>
            throw new InvalidOperationException(
                $"Construction contacted MongoDB through {targetMethod?.Name}.");
    }
}
