namespace MongoDB.AgentFramework;

/// <summary>Supported MongoDB RAG retrieval strategies.</summary>
public enum MongoDBSearchMode
{
    /// <summary>Approximate nearest-neighbor retrieval using <c>$vectorSearch</c>.</summary>
    VectorAnn,

    /// <summary>Exact nearest-neighbor retrieval using <c>$vectorSearch</c> with <c>exact: true</c>.</summary>
    VectorEnn,

    /// <summary>MongoDB Search full-text retrieval using <c>$search</c>.</summary>
    FullText,

    /// <summary>Native reciprocal-rank-fusion hybrid retrieval using <c>$rankFusion</c>.</summary>
    HybridRrf,
}
