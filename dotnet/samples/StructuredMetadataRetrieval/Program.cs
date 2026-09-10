using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.AgentFramework;
using MongoDB.Bson;

StructuredMetadataOptions command = StructuredMetadataOptions.Parse(args);
StructuredMetadataSettings settings = StructuredMetadataSettings.Load();
RetrievalPlan plan = RetrievalPlan.Parse();
var providerOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.FullText,
    SearchIndexName = settings.SearchIndexName,
    SearchTextFieldNames = ["text"],
    MetadataFieldNames = ["metadata.category", "visibility"],
    TopK = 3,
    MandatoryFilter = plan.ToMandatoryFilter(settings.TenantId),
};

await using var provider = new MongoDBRAGProvider(
    settings.ConnectionString,
    settings.DatabaseName,
    settings.CollectionName,
    providerOptions);

if (command.ValidateOnly)
{
    Console.WriteLine("Validated structured metadata retrieval configuration.");
    return;
}

await provider.ValidateSearchIndexAsync();
IReadOnlyList<MongoDBRAGResult> results = await provider.SearchAsync(plan.Query);
if (results.Count == 0)
{
    throw new InvalidOperationException(
        "No structured-match knowledge was found. Preload tenant-scoped Search documents with metadata.category " +
        "and visibility fields before running this sample.");
}

foreach (MongoDBRAGResult result in results)
{
    string category = result.Metadata.TryGetValue("metadata.category", out BsonValue? categoryValue)
        ? categoryValue.ToString() ?? "n/a"
        : "n/a";
    string visibility = result.Metadata.TryGetValue("visibility", out BsonValue? visibilityValue)
        ? visibilityValue.ToString() ?? "n/a"
        : "n/a";
    Console.WriteLine(
        $"[{result.SourceName ?? result.Id}] category={category} visibility={visibility} {result.Text}");
}

internal sealed record StructuredMetadataOptions(bool ValidateOnly)
{
    public static StructuredMetadataOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new(false);
        }

        if (args.Length == 1 && string.Equals(args[0], "--validate-only", StringComparison.Ordinal))
        {
            return new(true);
        }

        throw new ArgumentException("Usage: dotnet run --project ... -- [--validate-only]");
    }
}

internal sealed record StructuredMetadataSettings(
    string ConnectionString,
    string DatabaseName,
    string CollectionName,
    string SearchIndexName,
    string TenantId)
{
    public static StructuredMetadataSettings Load() =>
        new(
            Required("MONGODB_URI"),
            Required("MONGODB_DATABASE"),
            Required("MONGODB_RAG_COLLECTION"),
            Required("MONGODB_RAG_SEARCH_INDEX"),
            Required("MONGODB_RAG_TENANT"));

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name)?.Trim();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} before running structured metadata retrieval.");
    }
}

internal sealed class RetrievalPlan
{
    private const string SamplePlanJson = """
        {
          "query": "How is tenant access enforced?",
          "category": "security",
          "visibility": ["public"]
        }
        """;

    public string Query { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public List<VisibilityLevel> Visibility { get; init; } = [];

    public static RetrievalPlan Parse()
    {
        RetrievalPlan? plan = JsonSerializer.Deserialize<RetrievalPlan>(
            SamplePlanJson,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            });

        if (plan is null)
        {
            throw new InvalidOperationException("The sample retrieval plan could not be deserialized.");
        }

        plan.Validate();
        return plan;
    }

    public MongoDBRAGFilter ToMandatoryFilter(string tenantId)
    {
        Validate();
        return MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", tenantId),
            MongoDBRAGFilter.Equal("metadata.category", Category),
            MongoDBRAGFilter.In(
                "visibility",
                Visibility.Select(static value => (object)value.ToFilterValue()).ToArray()));
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            throw new InvalidOperationException("The retrieval plan query must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Category))
        {
            throw new InvalidOperationException("The retrieval plan category must not be empty.");
        }

        if (Visibility.Count == 0)
        {
            throw new InvalidOperationException("The retrieval plan must contain at least one approved visibility.");
        }
    }
}

internal enum VisibilityLevel
{
    Public,
    Internal,
}

internal static class VisibilityLevelExtensions
{
    public static string ToFilterValue(this VisibilityLevel visibility) =>
        visibility switch
        {
            VisibilityLevel.Public => "public",
            VisibilityLevel.Internal => "internal",
            _ => throw new ArgumentOutOfRangeException(nameof(visibility)),
        };
}
