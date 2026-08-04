using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework.Tests.Internal;

public sealed class CapabilityResultTests
{
    [Fact]
    public void Require_does_nothing_when_supported()
    {
        var result = new CapabilityResult("vector-search", supported: true);

        result.Require();
    }

    [Fact]
    public void Require_throws_actionable_error_when_unsupported()
    {
        var result = new CapabilityResult(
            "vector-search",
            supported: false,
            remediation: "Create the configured index.");

        MongoDBCapabilityException exception =
            Assert.Throws<MongoDBCapabilityException>(result.Require);

        Assert.Contains("Create the configured index.", exception.Message);
    }

    [Fact]
    public void Constructor_copies_detected_values()
    {
        var detected = new Dictionary<string, string> { ["state"] = "READY" };
        var result = new CapabilityResult(
            "vector-search",
            supported: true,
            detectedValues: detected);

        detected["state"] = "FAILED";

        Assert.Equal("READY", result.DetectedValues["state"]);
    }
}
