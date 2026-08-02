using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework.Tests.Internal;

public sealed class IndexNameTests
{
    [Theory]
    [InlineData("agent_framework_rag_vector")]
    [InlineData("_leading_underscore")]
    [InlineData("Mixed_Case-123")]
    [InlineData("a")]
    public void Validate_accepts_well_formed_index_names(string name)
    {
        Assert.Equal(name, IndexName.Validate(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_empty_names(string name)
    {
        Assert.Throws<MongoDBConfigurationException>(() => IndexName.Validate(name));
    }

    [Theory]
    [InlineData("bad\0name")]
    [InlineData("bad\nname")]
    [InlineData("bad\tname")]
    public void Validate_rejects_control_characters(string name)
    {
        Assert.Throws<MongoDBConfigurationException>(() => IndexName.Validate(name));
    }

    [Theory]
    [InlineData("$vectorSearch")]
    [InlineData("name$with$operators")]
    [InlineData("{$gt:1}")]
    public void Validate_rejects_operator_like_names(string name)
    {
        Assert.Throws<MongoDBConfigurationException>(() => IndexName.Validate(name));
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    public void Validate_rejects_separators_and_unsafe_syntax(string name)
    {
        Assert.Throws<MongoDBConfigurationException>(() => IndexName.Validate(name));
    }

    [Fact]
    public void Validate_rejects_names_starting_with_a_digit_or_hyphen()
    {
        Assert.Throws<MongoDBConfigurationException>(() => IndexName.Validate("1index"));
        Assert.Throws<MongoDBConfigurationException>(() => IndexName.Validate("-index"));
    }

    [Fact]
    public void Validate_rejects_excessively_long_names()
    {
        string tooLong = new('a', IndexName.MaxLength + 1);

        Assert.Throws<MongoDBConfigurationException>(() => IndexName.Validate(tooLong));
    }

    [Fact]
    public void Validate_accepts_a_name_at_the_maximum_length()
    {
        string maxLength = new('a', IndexName.MaxLength);

        Assert.Equal(maxLength, IndexName.Validate(maxLength));
    }
}
