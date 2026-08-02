using Microsoft.Agents.AI;

namespace MongoDB.AgentFramework.Tests.History;

public sealed class MongoDBChatHistoryConfigurationTests
{
    [Fact]
    public void ProviderUsesPublicFrameworkContract()
    {
        Assert.True(typeof(ChatHistoryProvider).IsAssignableFrom(typeof(MongoDBChatHistoryProvider)));
    }

    [Theory]
    [InlineData("", "agent", "session", "ApplicationId")]
    [InlineData("app", "", "session", "AgentId")]
    [InlineData("app", "agent", "", "SessionId")]
    public void OptionsRejectIncompleteAuthorizationScope(
        string applicationId,
        string agentId,
        string sessionId,
        string expectedName)
    {
        var options = new MongoDBChatHistoryProviderOptions
        {
            ApplicationId = applicationId,
            AgentId = agentId,
            SessionId = sessionId,
        };

        MongoDBConfigurationException exception = Assert.Throws<MongoDBConfigurationException>(
            options.Validate);

        Assert.Contains(expectedName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsRejectUnsafeLimitsAndDurations()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => (ValidOptions() with { MaxMessages = 0 }).Validate());
        Assert.Throws<MongoDBConfigurationException>(
            () => (ValidOptions() with { Retention = TimeSpan.Zero }).Validate());
        Assert.Throws<MongoDBConfigurationException>(
            () => (ValidOptions() with { RetrievalTimeout = TimeSpan.Zero }).Validate());
    }

    private static MongoDBChatHistoryProviderOptions ValidOptions() =>
        new()
        {
            ApplicationId = "app",
            AgentId = "agent",
            SessionId = "session",
        };
}
