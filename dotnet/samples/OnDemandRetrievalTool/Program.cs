using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using MongoDB.AgentFramework;

OnDemandOptions command = OnDemandOptions.Parse(args);
OnDemandSettings settings = OnDemandSettings.Load();
var providerOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.FullText,
    SearchIndexName = settings.SearchIndexName,
    SearchTextFieldNames = ["text"],
    TopK = 3,
    MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", settings.TenantId),
};

await using var provider = new MongoDBRAGProvider(
    settings.ConnectionString,
    settings.DatabaseName,
    settings.CollectionName,
    providerOptions);

AIFunction retrievalTool = BuildRetrievalTool(provider);
ValidateQueryOnlySchema(retrievalTool);

if (command.ValidateOnly)
{
    Console.WriteLine("Validated query-only retrieval tool configuration.");
    return;
}

await provider.ValidateSearchIndexAsync();
string rendered = await InvokeQueryOnlyToolAsync(retrievalTool, settings.QueryText);
if (string.IsNullOrWhiteSpace(rendered))
{
    throw new InvalidOperationException(
        "No authorized knowledge matched the sample query. Preload tenant-scoped Search documents before running " +
        "this sample.");
}

Console.WriteLine(rendered);

static AIFunction BuildRetrievalTool(MongoDBRAGProvider provider)
{
    async Task<string> RetrieveKnowledgeAsync(string query, CancellationToken cancellationToken)
    {
        IReadOnlyList<MongoDBRAGResult> results = await provider.SearchAsync(query, cancellationToken);
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            results.Select(static result =>
                $"[{result.SourceName ?? result.Id}] {result.Text}"));
    }

    return AIFunctionFactory.Create(
        (Func<string, CancellationToken, Task<string>>)RetrieveKnowledgeAsync,
        new AIFunctionFactoryOptions
        {
            Name = "retrieve_knowledge",
            Description = "Retrieve application-authorized knowledge for one natural-language query.",
            SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            },
        });
}

static async Task<string> InvokeQueryOnlyToolAsync(AIFunction retrievalTool, string queryText)
{
    object? invoked = await retrievalTool.InvokeAsync(
        new AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["query"] = queryText,
            }));

    return invoked switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
        JsonElement json => json.ToString(),
        _ => invoked?.ToString() ?? string.Empty,
    };
}

static void ValidateQueryOnlySchema(AIFunction retrievalTool)
{
    JsonElement schema = retrievalTool.JsonSchema;
    if (!schema.TryGetProperty("properties", out JsonElement properties))
    {
        throw new InvalidOperationException("The retrieval tool must expose a JSON schema with a properties object.");
    }

    string[] propertyNames = properties
        .EnumerateObject()
        .Select(static property => property.Name)
        .OrderBy(static name => name, StringComparer.Ordinal)
        .ToArray();
    if (propertyNames.Length != 1 || propertyNames[0] != "query")
    {
        throw new InvalidOperationException(
            "The retrieval tool schema must expose only the natural-language query parameter.");
    }

    if (!schema.TryGetProperty("required", out JsonElement required) ||
        !required.EnumerateArray().Any(static item => item.GetString() == "query"))
    {
        throw new InvalidOperationException("The retrieval tool must require the query parameter.");
    }
}

internal sealed record OnDemandOptions(bool ValidateOnly)
{
    public static OnDemandOptions Parse(string[] args)
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

internal sealed record OnDemandSettings(
    string ConnectionString,
    string DatabaseName,
    string CollectionName,
    string SearchIndexName,
    string TenantId,
    string QueryText)
{
    public static OnDemandSettings Load() =>
        new(
            Required("MONGODB_URI"),
            Required("MONGODB_DATABASE"),
            Required("MONGODB_RAG_COLLECTION"),
            Required("MONGODB_RAG_SEARCH_INDEX"),
            Required("MONGODB_RAG_TENANT"),
            "How is tenant access enforced?");

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name)?.Trim();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} before running on-demand retrieval.");
    }
}
