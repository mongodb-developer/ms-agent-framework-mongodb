using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class ChunkingOptionsTests
{
    [Fact]
    public void ValidateAcceptsDefaults()
    {
        new ChunkingOptions().Validate();
    }

    [Fact]
    public void ValidateRejectsNonPositiveWindowSize()
    {
        Assert.Throws<IngestionValidationException>(() => new ChunkingOptions { WindowSize = 0 }.Validate());
    }

    [Fact]
    public void ValidateRejectsNegativeOverlap()
    {
        Assert.Throws<IngestionValidationException>(() => new ChunkingOptions { OverlapSize = -1 }.Validate());
    }

    [Fact]
    public void ValidateRejectsOverlapEqualToWindow()
    {
        Assert.Throws<IngestionValidationException>(
            () => new ChunkingOptions { WindowSize = 100, OverlapSize = 100 }.Validate());
    }

    [Fact]
    public void ValidateRejectsOverlapGreaterThanWindow()
    {
        Assert.Throws<IngestionValidationException>(
            () => new ChunkingOptions { WindowSize = 100, OverlapSize = 150 }.Validate());
    }
}
