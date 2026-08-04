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
    public void FullTextIgnoresUnusedVectorConfiguration()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            // Vector index/field are "Not used" for FullText per the search-mode option contract; leaving them
            // blank (or otherwise invalid) must not fail validation for a mode that never reads them.
            VectorIndexName = string.Empty,
            VectorFieldName = string.Empty,
        };

        options.Validate();
    }

    [Theory]
    [InlineData(MongoDBSearchMode.VectorAnn)]
    [InlineData(MongoDBSearchMode.VectorEnn)]
    public void VectorOnlyModesIgnoreUnusedSearchConfiguration(MongoDBSearchMode mode)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = mode,
            // Search index/text fields are "Not used" for vector-only modes; leaving them blank must not fail
            // validation for a mode that never reads them.
            SearchIndexName = string.Empty,
            SearchTextFieldNames = [],
        };

        options.Validate();
    }

    [Fact]
    public void HybridRequiresVectorFieldMapping()
    {
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.HybridRrf };

        // Defaults already supply both branches; explicitly blank vector fields must fail validation because
        // Hybrid RRF requires both branches, unlike the single-branch modes.
        options.VectorFieldName = string.Empty;

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void HybridRequiresSearchFieldMapping()
    {
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.HybridRrf };

        // Defaults already supply both branches; explicitly blank search fields must fail validation because
        // Hybrid RRF requires both branches, unlike the single-branch modes.
        options.SearchTextFieldNames = [];

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void RejectsOperatorLikeVectorIndexName()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorIndexName = "$vectorSearch",
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void RejectsSearchIndexNameWithSeparators()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            SearchIndexName = "search/index",
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void RejectsExcessivelyLongIndexName()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorIndexName = new string('a', MongoDB.AgentFramework.Internal.IndexName.MaxLength + 1),
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

    [Theory]
    [InlineData(MongoDBSearchMode.VectorAnn)]
    [InlineData(MongoDBSearchMode.VectorEnn)]
    [InlineData(MongoDBSearchMode.FullText)]
    public void NonHybridModesForbidVectorCandidateLimit(MongoDBSearchMode mode)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = mode,
            VectorCandidateLimit = 50,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(MongoDBSearchMode.VectorAnn)]
    [InlineData(MongoDBSearchMode.VectorEnn)]
    [InlineData(MongoDBSearchMode.FullText)]
    public void NonHybridModesForbidTextCandidateLimit(MongoDBSearchMode mode)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = mode,
            TextCandidateLimit = 50,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(MongoDBSearchMode.VectorAnn)]
    [InlineData(MongoDBSearchMode.VectorEnn)]
    [InlineData(MongoDBSearchMode.FullText)]
    public void NonHybridModesForbidIncludeScoreDetails(MongoDBSearchMode mode)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = mode,
            IncludeScoreDetails = true,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MongoDBRAGProviderOptions.MaxNumCandidates + 1)]
    public void HybridVectorCandidateLimitMustBeBounded(int limit)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorCandidateLimit = limit,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MongoDBRAGProviderOptions.MaxNumCandidates + 1)]
    public void HybridTextCandidateLimitMustBeBounded(int limit)
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            TextCandidateLimit = limit,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void HybridAcceptsExplicitCandidateLimitsAndScoreDetails()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorCandidateLimit = 100,
            TextCandidateLimit = 100,
            IncludeScoreDetails = true,
        };

        options.Validate();
    }

    [Fact]
    public void HybridRejectsAnExplicitVectorCandidateLimitAboveTheDefaultNumCandidates()
    {
        // NumCandidates is left unset, so its effective value is the default over-fetch heuristic (100 for the
        // default TopK of 5); $vectorSearch requires numCandidates >= limit, so a VectorCandidateLimit above that
        // default must be rejected rather than silently sent to MongoDB as an invalid pipeline.
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorCandidateLimit = 101,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void HybridRejectsAnExplicitNumCandidatesBelowTheDefaultVectorCandidateLimit()
    {
        // VectorCandidateLimit is left unset (default 100), so an explicit NumCandidates below that default must
        // be rejected even though NumCandidates alone satisfies the separate NumCandidates >= TopK check.
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            TopK = 5,
            NumCandidates = 50,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void HybridRejectsAnExplicitNumCandidatesBelowAnExplicitVectorCandidateLimit()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            NumCandidates = 100,
            VectorCandidateLimit = 150,
        };

        Assert.Throws<MongoDBConfigurationException>(options.Validate);
    }

    [Fact]
    public void HybridAcceptsExplicitNumCandidatesEqualToTheExplicitVectorCandidateLimit()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            NumCandidates = 150,
            VectorCandidateLimit = 150,
        };

        options.Validate();
    }

    [Fact]
    public void HybridAcceptsDefaultNumCandidatesAndVectorCandidateLimitTogether()
    {
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.HybridRrf };

        options.Validate();
    }

    [Fact]
    public void CopyPreservesHybridCandidateLimitsAndScoreDetails()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorCandidateLimit = 42,
            TextCandidateLimit = 84,
            IncludeScoreDetails = true,
        };

        MongoDBRAGProviderOptions copy = options.Copy();

        Assert.Equal(42, copy.VectorCandidateLimit);
        Assert.Equal(84, copy.TextCandidateLimit);
        Assert.True(copy.IncludeScoreDetails);
    }
}
