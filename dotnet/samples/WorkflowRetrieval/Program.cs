using Microsoft.Agents.AI.Workflows;
using MongoDB.AgentFramework;

WorkflowRetrievalOptions command = WorkflowRetrievalOptions.Parse(args);
WorkflowRetrievalSettings settings = WorkflowRetrievalSettings.Load();
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

async ValueTask<string> RetrieveAsync(string query, CancellationToken cancellationToken)
{
    IReadOnlyList<MongoDBRAGResult> results = await provider.SearchAsync(query, cancellationToken);
    if (results.Count == 0)
    {
        throw new InvalidOperationException(
            "No authorized knowledge matched the workflow query. Preload tenant-scoped Search documents before " +
            "running this sample.");
    }

    return string.Join(
        Environment.NewLine + Environment.NewLine,
        results.Select(static result => $"[{result.SourceName ?? result.Id}] {result.Text}"));
}

ExecutorBinding retrievalStep =
    ((Func<string, CancellationToken, ValueTask<string>>)RetrieveAsync)
    .BindAsExecutor(id: "mongodb-retrieval-step", threadsafe: true);

Workflow workflow = new WorkflowBuilder(retrievalStep)
    .WithName("workflow-retrieval")
    .WithDescription("Deterministic direct MongoDB retrieval inside a workflow step.")
    .WithOutputFrom(retrievalStep)
    .Build(validateOrphans: true);

if (command.ValidateOnly)
{
    Console.WriteLine("Validated workflow retrieval configuration.");
    return;
}

await provider.ValidateSearchIndexAsync();
await using Run run = await InProcessExecution.RunAsync(
    workflow,
    settings.QueryText,
    sessionId: "workflow-retrieval-sample");

string output = run.OutgoingEvents
    .OfType<WorkflowOutputEvent>()
    .Select(static workflowEvent => workflowEvent.As<string>())
    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
    ?? throw new InvalidOperationException("The workflow completed without yielding a retrieval result.");

Console.WriteLine(output);

internal sealed record WorkflowRetrievalOptions(bool ValidateOnly)
{
    public static WorkflowRetrievalOptions Parse(string[] args)
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

internal sealed record WorkflowRetrievalSettings(
    string ConnectionString,
    string DatabaseName,
    string CollectionName,
    string SearchIndexName,
    string TenantId,
    string QueryText)
{
    public static WorkflowRetrievalSettings Load() =>
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
            : throw new InvalidOperationException($"Set {name} before running workflow retrieval.");
    }
}
