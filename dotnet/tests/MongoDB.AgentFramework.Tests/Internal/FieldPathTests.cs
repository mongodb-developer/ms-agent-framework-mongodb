using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.Internal;

public sealed class FieldPathTests
{
    [Theory]
    [InlineData("")]
    [InlineData("source..title")]
    [InlineData("$source.title")]
    [InlineData("items.0.name")]
    [InlineData("items.$[].name")]
    [InlineData("metadata._ragScore")]
    public void Validate_rejects_unsafe_paths(string path)
    {
        Assert.Throws<MongoDBConfigurationException>(() => FieldPath.Validate(path));
    }

    [Fact]
    public void Resolve_returns_nested_value()
    {
        var document = new BsonDocument("source", new BsonDocument("title", "Example"));

        BsonValue value = FieldPath.Resolve(document, "source.title");

        Assert.Equal("Example", value.AsString);
    }

    [Fact]
    public void Resolve_rejects_missing_value()
    {
        var document = new BsonDocument("source", new BsonDocument());

        Assert.Throws<MongoDBMappingException>(
            () => FieldPath.Resolve(document, "source.title"));
    }
}
