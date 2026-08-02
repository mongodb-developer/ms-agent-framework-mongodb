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

    [Fact]
    public void TryResolve_returns_true_and_nested_value_when_present()
    {
        var document = new BsonDocument("source", new BsonDocument("title", "Example"));

        bool found = FieldPath.TryResolve(document, "source.title", out BsonValue? value);

        Assert.True(found);
        Assert.Equal("Example", value!.AsString);
    }

    [Fact]
    public void TryResolve_returns_false_without_throwing_when_missing()
    {
        var document = new BsonDocument("source", new BsonDocument());

        bool found = FieldPath.TryResolve(document, "source.title", out BsonValue? value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void TryResolve_returns_false_when_an_intermediate_segment_is_not_a_document()
    {
        var document = new BsonDocument("source", "not-a-document");

        bool found = FieldPath.TryResolve(document, "source.title", out BsonValue? value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void TryResolve_still_validates_the_path()
    {
        var document = new BsonDocument();

        Assert.Throws<MongoDBConfigurationException>(
            () => FieldPath.TryResolve(document, "$bad", out _));
    }
}
