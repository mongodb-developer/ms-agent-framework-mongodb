namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class MongoDBRAGProviderOptionsTests
{
    [Theory]
    [InlineData(MongoDBSearchMode.VectorAnn)]
    [InlineData(MongoDBSearchMode.VectorEnn)]
    [InlineData(MongoDBSearchMode.FullText)]
    [InlineData(MongoDBSearchMode.HybridRrf)]
    public void DefaultsAreValidForEveryMode(MongoDBSearchMode mode)
    {
        var options = new MongoDBRAGProviderOptions { SearchMode = mode };

        options.Validate();
    }

    [Fact]
    public void VectorEnnForbidsExplicitNumCandidates()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorEnn,
            NumCandidates = 50,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void FullTextForbidsNumCandidates()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            NumCandidates = 50,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(MongoDBSearchMode.VectorAnn)]
    [InlineData(MongoDBSearchMode.HybridRrf)]
    public void VectorCandidatesMustBeAtLeastTopK(MongoDBSearchMode mode)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = mode,
            TopK = 10,
            NumCandidates = 9,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void TopKMustBeBounded(int topK)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            TopK = topK,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void HybridRequiresVectorAndSearchFieldMappings()
    {
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.HybridRrf };

        // Defaults already supply both branches; explicitly blank fields must fail validation.
        options.VectorFieldName = string.Empty;

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void HybridRejectsWhenBothWeightsAreZero()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorWeight = 0,
            TextWeight = 0,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void HybridAcceptsOneZeroWeightWhenTheOtherIsPositive()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorWeight = 0,
            TextWeight = 2.0,
        };

        options.Validate();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void WeightsMustBeFiniteAndNonNegative(double weight)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorWeight = weight,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void RejectsInvalidFieldPaths()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorFieldName = "$bad",
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void RejectsSearchTextFieldNamesCollidingWithReservedAlias()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            SearchTextFieldNames = ["_ragScore"],
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void RejectsEmptySearchTextFieldNames()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            SearchTextFieldNames = [],
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void RetrievalTimeoutMustBePositiveWhenConfigured()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            RetrievalTimeout = TimeSpan.Zero,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void CopyReturnsIndependentValidatedSnapshot()
    {
        var metadataFields = new List<string> { "category" };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            MetadataFieldNames = metadataFields,
        };

        MongoDBRAGProviderOptions copy = options.Copy();
        metadataFields.Add("added_after_copy");

        Assert.Single(copy.MetadataFieldNames!);
        Assert.Equal("category", copy.MetadataFieldNames![0]);
    }

    [Fact]
    public void CopyValidatesBeforeSnapshotting()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            TopK = 0,
        };

        Assert.Throws<MongoDBConfigurationException>(() => options.Copy());
    }

    [Fact]
    public void CopyPreservesMandatoryFilter()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a");
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            MandatoryFilter = filter,
        };

        MongoDBRAGProviderOptions copy = options.Copy();

        Assert.Same(filter, copy.MandatoryFilter);
    }
}
