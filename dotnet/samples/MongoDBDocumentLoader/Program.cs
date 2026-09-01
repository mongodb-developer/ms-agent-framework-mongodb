using System.Runtime.CompilerServices;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

LoaderCommand command = LoaderCommand.Parse(args);
LoaderSettings settings = LoaderSettings.Load();

using var client = new MongoClient(settings.ConnectionString);
IMongoCollection<BsonDocument> collection = client
    .GetDatabase(settings.DatabaseName)
    .GetCollection<BsonDocument>(settings.CollectionName);
var loader = new SampleMongoDBDocumentLoader(
    collection,
    settings.SamplePrefix,
    command.PageSize,
    settings.SourceIdField,
    settings.ContentField,
    settings.TitleField,
    settings.UrlField,
    settings.MetadataField,
    settings.TenantField,
    settings.DeletedField);

if (command.ValidateOnly)
{
    Console.WriteLine("Validated MongoDB document loader configuration.");
    return;
}

using var cancellationSource = new CancellationTokenSource();
int loaded = 0;
try
{
    await foreach (IngestionDocument document in loader.LoadAsync(cancellationSource.Token))
    {
        Console.WriteLine(
            $"{document.SourceId}: title=\"{document.Title}\", tenant=\"{document.TenantId}\", " +
            $"deleted={document.Deleted}, metadataKeys={document.Metadata.Count}");
        loaded++;

        if (command.CancelAfterDocuments is int cancelAfter && loaded >= cancelAfter)
        {
            cancellationSource.Cancel();
        }

        if (loaded >= command.MaxDocuments)
        {
            break;
        }
    }
}
catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
{
    Console.WriteLine($"Cancelled after mapping {loaded} bounded source document(s).");
    return;
}

if (loaded == 0)
{
    throw new InvalidOperationException(
        "No sample-prefixed source documents were found. Insert source records within the configured prefix before " +
        "running this sample.");
}

Console.WriteLine($"Mapped {loaded} bounded source document(s).");

internal sealed record LoaderCommand(
    bool ValidateOnly,
    int PageSize,
    int MaxDocuments,
    int? CancelAfterDocuments)
{
    public static LoaderCommand Parse(string[] args)
    {
        bool validateOnly = false;
        int pageSize = 100;
        int maxDocuments = 10;
        int? cancelAfterDocuments = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--validate-only":
                    validateOnly = true;
                    break;
                case "--page-size":
                    pageSize = ParseBoundedInt(args, ref index, "--page-size");
                    break;
                case "--max-documents":
                    maxDocuments = ParseBoundedInt(args, ref index, "--max-documents");
                    break;
                case "--cancel-after-documents":
                    cancelAfterDocuments = ParseBoundedInt(args, ref index, "--cancel-after-documents");
                    break;
                default:
                    throw new ArgumentException(
                        "Usage: dotnet run --project ... -- [--validate-only] [--page-size 1-1000] " +
                        "[--max-documents 1-1000] [--cancel-after-documents 1-1000]");
            }
        }

        return new(validateOnly, pageSize, maxDocuments, cancelAfterDocuments);
    }

    private static int ParseBoundedInt(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value is < 1 or > 1000)
        {
            throw new ArgumentException($"{name} must be followed by an integer from 1 through 1000.");
        }

        index++;
        return value;
    }
}

internal sealed record LoaderSettings(
    string ConnectionString,
    string DatabaseName,
    string CollectionName,
    string SamplePrefix,
    string SourceIdField,
    string ContentField,
    string TitleField,
    string UrlField,
    string MetadataField,
    string TenantField,
    string DeletedField)
{
    public static LoaderSettings Load() =>
        new(
            Required("MONGODB_URI"),
            Required("MONGODB_DATABASE"),
            Required("MONGODB_INGESTION_SOURCE_COLLECTION"),
            Required("MONGODB_RAG_SAMPLE_PREFIX"),
            Optional("MONGODB_INGESTION_SOURCE_ID_FIELD", "source_id"),
            Optional("MONGODB_INGESTION_CONTENT_FIELD", "content"),
            Optional("MONGODB_INGESTION_TITLE_FIELD", "title"),
            Optional("MONGODB_INGESTION_URL_FIELD", "url"),
            Optional("MONGODB_INGESTION_METADATA_FIELD", "metadata"),
            Optional("MONGODB_INGESTION_TENANT_FIELD", "tenant_id"),
            Optional("MONGODB_INGESTION_DELETED_FIELD", "deleted"));

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name)?.Trim();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} before running the MongoDB document loader.");
    }

    private static string Optional(string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

internal sealed record IngestionDocument(
    string SourceId,
    string Content,
    string Title,
    string Url,
    IReadOnlyDictionary<string, BsonValue> Metadata,
    string TenantId,
    bool Deleted);

internal sealed class SampleMongoDBDocumentLoader
{
    private static readonly Collation SimpleBinaryCollation = new("simple");

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly string _samplePrefix;
    private readonly string _prefixUpperBound;
    private readonly int _pageSize;
    private readonly string _sourceIdField;
    private readonly string _contentField;
    private readonly string _titleField;
    private readonly string _urlField;
    private readonly string _metadataField;
    private readonly string _tenantField;
    private readonly string _deletedField;

    public SampleMongoDBDocumentLoader(
        IMongoCollection<BsonDocument> collection,
        string samplePrefix,
        int pageSize,
        string sourceIdField,
        string contentField,
        string titleField,
        string urlField,
        string metadataField,
        string tenantField,
        string deletedField)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        if (!samplePrefix.StartsWith("sample-", StringComparison.Ordinal) &&
            !samplePrefix.StartsWith("test-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MONGODB_RAG_SAMPLE_PREFIX must start with 'sample-' or 'test-'.");
        }

        if (pageSize is < 1 or > 1000)
        {
            throw new InvalidOperationException("pageSize must be an integer from 1 through 1000.");
        }

        _samplePrefix = samplePrefix;
        _prefixUpperBound = PrefixUpperBound(samplePrefix);
        _pageSize = pageSize;
        _sourceIdField = ValidateFieldPath(sourceIdField, nameof(sourceIdField));
        _contentField = ValidateFieldPath(contentField, nameof(contentField));
        _titleField = ValidateFieldPath(titleField, nameof(titleField));
        _urlField = ValidateFieldPath(urlField, nameof(urlField));
        _metadataField = ValidateFieldPath(metadataField, nameof(metadataField));
        _tenantField = ValidateFieldPath(tenantField, nameof(tenantField));
        _deletedField = ValidateFieldPath(deletedField, nameof(deletedField));
    }

    public async IAsyncEnumerable<IngestionDocument> LoadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ValidateUniqueSourceIdsAsync(cancellationToken);

        string? lastSourceId = null;
        ProjectionDefinition<BsonDocument> projection = Builders<BsonDocument>.Projection
            .Include(_sourceIdField)
            .Include(_contentField)
            .Include(_titleField)
            .Include(_urlField)
            .Include(_metadataField)
            .Include(_tenantField)
            .Include(_deletedField);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FilterDefinition<BsonDocument> filter = BuildPageFilter(lastSourceId);
            List<BsonDocument> page = await _collection
                .Find(filter, new FindOptions { Collation = SimpleBinaryCollation })
                .Project(projection)
                .Sort(Builders<BsonDocument>.Sort.Ascending(_sourceIdField))
                .Limit(_pageSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                yield break;
            }

            foreach (BsonDocument item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sourceId = ResolveRequiredString(item, _sourceIdField);
                BsonDocument metadata = ResolveOptionalDocument(item, _metadataField);
                yield return new IngestionDocument(
                    sourceId,
                    ResolveRequiredString(item, _contentField),
                    ResolveRequiredString(item, _titleField),
                    ResolveRequiredString(item, _urlField),
                    metadata.Elements.ToDictionary(
                        static element => element.Name,
                        static element => (BsonValue)element.Value,
                        StringComparer.Ordinal),
                    ResolveRequiredString(item, _tenantField),
                    ResolveOptionalBoolean(item, _deletedField));
                lastSourceId = sourceId;
            }
        }
    }

    private FilterDefinition<BsonDocument> BuildPageFilter(string? lastSourceId)
    {
        var bounds = new BsonDocument
        {
            { "$gte", _samplePrefix },
            { "$lt", _prefixUpperBound },
        };
        if (!string.IsNullOrWhiteSpace(lastSourceId))
        {
            bounds["$gt"] = lastSourceId;
        }

        return new BsonDocument(_sourceIdField, bounds);
    }

    private async Task ValidateUniqueSourceIdsAsync(CancellationToken cancellationToken)
    {
        BsonDocument match = new(
            "$match",
            new BsonDocument(
                _sourceIdField,
                new BsonDocument
                {
                    { "$gte", _samplePrefix },
                    { "$lt", _prefixUpperBound },
                }));
        BsonDocument group = new(
            "$group",
            new BsonDocument
            {
                { "_id", $"${_sourceIdField}" },
                { "count", new BsonDocument("$sum", 1) },
            });
        BsonDocument duplicatesOnly = new(
            "$match",
            new BsonDocument("count", new BsonDocument("$gt", 1)));
        BsonDocument limit = new("$limit", 1);

        List<BsonDocument> duplicates = await _collection
            .Aggregate(new AggregateOptions { Collation = SimpleBinaryCollation })
            .AppendStage<BsonDocument>(match)
            .AppendStage<BsonDocument>(group)
            .AppendStage<BsonDocument>(duplicatesOnly)
            .AppendStage<BsonDocument>(limit)
            .ToListAsync(cancellationToken);

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Source documents contain a duplicate source ID within the configured sample prefix.");
        }
    }

    private static string ValidateFieldPath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} must be a non-empty safe field path.");
        }

        foreach (string segment in value.Split('.'))
        {
            if (string.IsNullOrWhiteSpace(segment) ||
                segment.StartsWith('$') ||
                segment == "$[]" ||
                int.TryParse(segment, out _))
            {
                throw new InvalidOperationException($"{name} must be a safe field path.");
            }
        }

        return value;
    }

    private static string ResolveRequiredString(BsonDocument document, string path)
    {
        if (!TryResolve(document, path, out BsonValue? value) || value is null || !value.IsString)
        {
            throw new InvalidOperationException($"Source document is missing configured string field '{path}'.");
        }

        return value.AsString;
    }

    private static BsonDocument ResolveOptionalDocument(BsonDocument document, string path)
    {
        if (!TryResolve(document, path, out BsonValue? value) || value is null || value.IsBsonNull)
        {
            return new BsonDocument();
        }

        return value.AsBsonDocument;
    }

    private static bool ResolveOptionalBoolean(BsonDocument document, string path) =>
        TryResolve(document, path, out BsonValue? value) &&
        value is not null &&
        !value.IsBsonNull &&
        value.ToBoolean();

    private static bool TryResolve(BsonDocument document, string path, out BsonValue? value)
    {
        BsonValue current = document;
        foreach (string segment in path.Split('.'))
        {
            if (current is not BsonDocument currentDocument || !currentDocument.TryGetValue(segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static string PrefixUpperBound(string prefix)
    {
        _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetByteCount(prefix);

        List<Rune> runes = prefix.EnumerateRunes().ToList();
        for (int index = runes.Count - 1; index >= 0; index--)
        {
            int successor = runes[index].Value + 1;
            if (successor > 0x10FFFF)
            {
                continue;
            }

            if (successor is >= 0xD800 and <= 0xDFFF)
            {
                successor = 0xE000;
            }

            var builder = new StringBuilder();
            for (int prefixIndex = 0; prefixIndex < index; prefixIndex++)
            {
                builder.Append(runes[prefixIndex].ToString());
            }

            builder.Append(new Rune(successor).ToString());
            return builder.ToString();
        }

        throw new InvalidOperationException("MONGODB_RAG_SAMPLE_PREFIX has no exclusive Unicode successor.");
    }
}
