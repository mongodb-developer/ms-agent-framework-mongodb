using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class ParentContextBoundingOptionsTests
{
    [Fact]
    public void ValidateAcceptsTheDefaultOptions()
    {
        new ParentContextBoundingOptions().Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRejectsNonPositiveMaxCharactersPerParent(int value)
    {
        var options = new ParentContextBoundingOptions { MaxCharactersPerParent = value };
        Assert.Throws<IngestionValidationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRejectsNonPositiveMaxTotalContextCharacters(int value)
    {
        var options = new ParentContextBoundingOptions { MaxTotalContextCharacters = value };
        Assert.Throws<IngestionValidationException>(options.Validate);
    }
}
