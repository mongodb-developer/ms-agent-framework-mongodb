using System.Runtime.CompilerServices;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A bounded, paged, cancellable local source reader: reads <c>*.txt</c> files from one directory in fixed-size
/// pages instead of loading every file up front, so ingesting a large local sample corpus never holds an unbounded
/// number of documents in memory at once. This is a sample-local, offline-friendly stand-in for the "application
/// owns parsing" step of docs/spec/features/ingestion.md's ingestion pipeline; production sources (crawlers,
/// databases, document stores) are explicitly out of scope for the runtime package.
/// </summary>
public sealed class BoundedFileSystemSourceReader
{
    private readonly string _directoryPath;
    private readonly string _tenantId;
    private readonly int _pageSize;

    /// <summary>Initializes a reader over one local directory.</summary>
    /// <param name="directoryPath">The directory to enumerate <c>*.txt</c> files from.</param>
    /// <param name="tenantId">The tenant every read <see cref="SourceDocument"/> is stamped with.</param>
    /// <param name="pageSize">The maximum number of documents materialized per page. Must be positive.</param>
    public BoundedFileSystemSourceReader(string directoryPath, string tenantId, int pageSize = 10)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new IngestionValidationException($"{nameof(directoryPath)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new IngestionValidationException($"{nameof(tenantId)} must not be empty.");
        }

        if (pageSize <= 0)
        {
            throw new IngestionValidationException($"{nameof(pageSize)} must be positive.");
        }

        _directoryPath = directoryPath;
        _tenantId = tenantId;
        _pageSize = pageSize;
    }

    /// <summary>
    /// Streams bounded pages of at most the configured page size, ordered deterministically by file name, checking
    /// cancellation before each page and before each file read within a page.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<SourceDocument>> ReadPagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string[] files = Directory.Exists(_directoryPath)
            ? [.. Directory.GetFiles(_directoryPath, "*.txt").OrderBy(static path => path, StringComparer.Ordinal)]
            : [];

        for (int offset = 0; offset < files.Length; offset += _pageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = new List<SourceDocument>();
            foreach (string file in files.Skip(offset).Take(_pageSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                string sourceId = Path.GetFileNameWithoutExtension(file);
                page.Add(new SourceDocument(
                    TenantId: _tenantId,
                    SourceId: sourceId,
                    Content: content,
                    Title: sourceId,
                    Url: new Uri(Path.GetFullPath(file)).AbsoluteUri));
            }

            yield return page;
        }
    }
}
