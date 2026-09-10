using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework.Tests.Internal;

public sealed class MongoClientFactoryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void From_connection_string_rejects_empty_values(string value)
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => MongoClientFactory.FromConnectionString(value));
    }

    [Fact]
    public void From_connection_string_wraps_factory_errors()
    {
        InvalidOperationException cause = new("bad settings");

        MongoDBConfigurationException exception =
            Assert.Throws<MongoDBConfigurationException>(
                () => MongoClientFactory.FromConnectionString(
                    "mongodb://localhost",
                    _ => throw cause));

        Assert.Same(cause, exception.InnerException);
    }
}
