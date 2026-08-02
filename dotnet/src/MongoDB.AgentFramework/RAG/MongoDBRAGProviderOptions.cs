namespace MongoDB.AgentFramework;

/// <summary>Configuration for MongoDB RAG direct search, with mode-specific defaults and validation.</summary>
public sealed class MongoDBRAGProviderOptions
{
    /// <summary>The maximum accepted final result count.</summary>
    public const int MaxTopK = 1000;

    /// <summary>The maximum accepted ANN candidate count.</summary>
    public const int MaxNumCandidates = 10_000;

    /// <summary>The maximum number of full-text search field paths.</summary>
    public const int MaxSearchTextFieldNames = 20;

    /// <summary>The maximum number of metadata field paths.</summary>
    public const int MaxMetadataFieldNames = 50;

    /// <summary>Gets or sets the retrieval strategy.</summary>
    public required MongoDBSearchMode SearchMode { get; set; }

    /// <summary>Gets or sets the Vector Search index name used by vector and hybrid modes.</summary>
    public string VectorIndexName { get; set; } = "agent_framework_rag_vector";

    /// <summary>Gets or sets the Search index name used by full-text and hybrid modes.</summary>
    public string SearchIndexName { get; set; } = "agent_framework_rag_search";

    /// <summary>Gets or sets the embedding field path used by vector and hybrid modes.</summary>
    public string VectorFieldName { get; set; } = "embedding";

    /// <summary>Gets or sets the text field paths queried by full-text and hybrid modes.</summary>
    public IReadOnlyList<string> SearchTextFieldNames { get; set; } = ["text"];

    /// <summary>Gets or sets the document identifier field path.</summary>
    public string IdFieldName { get; set; } = "_id";

    /// <summary>Gets or sets the chunk text field path mapped to <see cref="MongoDBRAGResult.Text"/>.</summary>
    public string ChunkTextFieldName { get; set; } = "text";

    /// <summary>Gets or sets the optional source title/name field path.</summary>
    public string? SourceNameFieldName { get; set; } = "source.name";

    /// <summary>Gets or sets the optional source URL field path.</summary>
    public string? SourceUrlFieldName { get; set; } = "source.url";

    /// <summary>Gets or sets optional metadata field paths mapped into <see cref="MongoDBRAGResult.Metadata"/>.</summary>
    public IReadOnlyList<string>? MetadataFieldNames { get; set; }

    /// <summary>Gets or sets the final result limit, from 1 through <see cref="MaxTopK"/>.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Gets or sets ANN candidates for <see cref="MongoDBSearchMode.VectorAnn"/> and the vector input of
    /// <see cref="MongoDBSearchMode.HybridRrf"/>. Must be null for <see cref="MongoDBSearchMode.VectorEnn"/> and
    /// <see cref="MongoDBSearchMode.FullText"/>.
    /// </summary>
    public int? NumCandidates { get; set; }

    /// <summary>Gets or sets the hybrid vector-input fusion weight. Defaults to 1.0.</summary>
    public double VectorWeight { get; set; } = 1.0;

    /// <summary>Gets or sets the hybrid text-input fusion weight. Defaults to 1.0.</summary>
    public double TextWeight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the caller-configured mandatory filter translated into every active retrieval branch. This is
    /// the sole supported mechanism for tenant and authorization constraints; it must never be derived from raw
    /// BSON or model output.
    /// </summary>
    public MongoDBRAGFilter? MandatoryFilter { get; set; }

    /// <summary>Gets or sets an optional complete retrieval deadline.</summary>
    public TimeSpan? RetrievalTimeout { get; set; }

    /// <summary>Validates all options without contacting MongoDB.</summary>
    public void Validate()
    {
        Internal.FieldPath.Validate(IdFieldName, nameof(IdFieldName));
        Internal.FieldPath.Validate(ChunkTextFieldName, nameof(ChunkTextFieldName));
        if (SourceNameFieldName is not null)
        {
            Internal.FieldPath.Validate(SourceNameFieldName, nameof(SourceNameFieldName));
        }

        if (SourceUrlFieldName is not null)
        {
            Internal.FieldPath.Validate(SourceUrlFieldName, nameof(SourceUrlFieldName));
        }

        ValidateMetadataFieldNames();

        if (TopK is < 1 or > MaxTopK)
        {
            throw new MongoDBConfigurationException($"TopK must be between 1 and {MaxTopK}.");
        }

        // Only validate the vector/search configuration a mode actually reads, per the search-mode option contract
        // in docs/spec/features/rag.md: vector-only modes must not require search index/field configuration, and
        // FullText must not require vector index/field configuration. Hybrid RRF is the only mode that reads both.
        switch (SearchMode)
        {
            case MongoDBSearchMode.VectorAnn:
                ValidateVectorConfiguration();
                ValidateNumCandidates();
                break;
            case MongoDBSearchMode.VectorEnn:
                ValidateVectorConfiguration();
                if (NumCandidates is not null)
                {
                    throw new MongoDBConfigurationException(
                        "NumCandidates must not be set for VectorEnn (exact) search.");
                }

                break;
            case MongoDBSearchMode.FullText:
                ValidateSearchConfiguration();
                if (NumCandidates is not null)
                {
                    throw new MongoDBConfigurationException(
                        "NumCandidates is not used with FullText search.");
                }

                break;
            case MongoDBSearchMode.HybridRrf:
                ValidateVectorConfiguration();
                ValidateSearchConfiguration();
                ValidateNumCandidates();
                if (VectorWeight <= 0 && TextWeight <= 0)
                {
                    throw new MongoDBConfigurationException(
                        "At least one of VectorWeight or TextWeight must be greater than zero.");
                }

                break;
            default:
                throw new MongoDBConfigurationException($"Unsupported search mode '{SearchMode}'.");
        }

        ValidateWeight(VectorWeight, nameof(VectorWeight));
        ValidateWeight(TextWeight, nameof(TextWeight));

        if (RetrievalTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new MongoDBConfigurationException("RetrievalTimeout must be positive when configured.");
        }
    }

    /// <summary>Validates this instance and returns an independent, immutable snapshot copy.</summary>
    internal MongoDBRAGProviderOptions Copy()
    {
        Validate();
        return new MongoDBRAGProviderOptions
        {
            SearchMode = SearchMode,
            VectorIndexName = VectorIndexName,
            SearchIndexName = SearchIndexName,
            VectorFieldName = VectorFieldName,
            SearchTextFieldNames = [.. SearchTextFieldNames],
            IdFieldName = IdFieldName,
            ChunkTextFieldName = ChunkTextFieldName,
            SourceNameFieldName = SourceNameFieldName,
            SourceUrlFieldName = SourceUrlFieldName,
            MetadataFieldNames = MetadataFieldNames is null ? null : [.. MetadataFieldNames],
            TopK = TopK,
            NumCandidates = NumCandidates,
            VectorWeight = VectorWeight,
            TextWeight = TextWeight,
            MandatoryFilter = MandatoryFilter,
            RetrievalTimeout = RetrievalTimeout,
        };
    }

    private void ValidateVectorConfiguration()
    {
        Internal.IndexName.Validate(VectorIndexName, nameof(VectorIndexName));
        Internal.FieldPath.Validate(VectorFieldName, nameof(VectorFieldName));
    }

    private void ValidateSearchConfiguration()
    {
        Internal.IndexName.Validate(SearchIndexName, nameof(SearchIndexName));
        ValidateSearchTextFieldNames();
    }

    private void ValidateNumCandidates()
    {
        if (NumCandidates is not { } candidates)
        {
            return;
        }

        if (candidates is < 1 or > MaxNumCandidates)
        {
            throw new MongoDBConfigurationException(
                $"NumCandidates must be between 1 and {MaxNumCandidates}.");
        }

        if (candidates < TopK)
        {
            throw new MongoDBConfigurationException("NumCandidates must be at least TopK.");
        }
    }

    private void ValidateSearchTextFieldNames()
    {
        if (SearchTextFieldNames is null || SearchTextFieldNames.Count == 0)
        {
            throw new MongoDBConfigurationException(
                "SearchTextFieldNames must contain at least one field path.");
        }

        if (SearchTextFieldNames.Count > MaxSearchTextFieldNames)
        {
            throw new MongoDBConfigurationException(
                $"SearchTextFieldNames must not exceed {MaxSearchTextFieldNames} entries.");
        }

        foreach (string field in SearchTextFieldNames)
        {
            Internal.FieldPath.Validate(field, nameof(SearchTextFieldNames));
        }
    }

    private void ValidateMetadataFieldNames()
    {
        if (MetadataFieldNames is null)
        {
            return;
        }

        if (MetadataFieldNames.Count > MaxMetadataFieldNames)
        {
            throw new MongoDBConfigurationException(
                $"MetadataFieldNames must not exceed {MaxMetadataFieldNames} entries.");
        }

        foreach (string field in MetadataFieldNames)
        {
            Internal.FieldPath.Validate(field, nameof(MetadataFieldNames));
        }
    }

    private static void ValidateWeight(double weight, string name)
    {
        if (double.IsNaN(weight) || double.IsInfinity(weight))
        {
            throw new MongoDBConfigurationException($"{name} must be finite.");
        }

        if (weight < 0)
        {
            throw new MongoDBConfigurationException($"{name} must not be negative.");
        }
    }
}
