using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class BoundedFileSystemSourceReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("af-ingestion-samples-tests-");

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    [Fact]
    public async Task ReadPagesAsyncReturnsEveryFileExactlyOnceAcrossPages()
    {
        for (int i = 0; i < 7; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_directory.FullName, $"doc-{i:D2}.txt"), $"content {i}");
        }

        var reader = new BoundedFileSystemSourceReader(_directory.FullName, "tenant-a", pageSize: 3);
        var sourceIds = new List<string>();
        await foreach (IReadOnlyList<SourceDocument> page in reader.ReadPagesAsync())
        {
            sourceIds.AddRange(page.Select(document => document.SourceId));
        }

        Assert.Equal(7, sourceIds.Count);
        Assert.Equal(sourceIds.Count, sourceIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ReadPagesAsyncHonorsThePageSizeBound()
    {
        for (int i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_directory.FullName, $"doc-{i:D2}.txt"), $"content {i}");
        }

        var reader = new BoundedFileSystemSourceReader(_directory.FullName, "tenant-a", pageSize: 2);
        var pageSizes = new List<int>();
        await foreach (IReadOnlyList<SourceDocument> page in reader.ReadPagesAsync())
        {
            pageSizes.Add(page.Count);
        }

        Assert.Equal([2, 2, 1], pageSizes);
    }

    [Fact]
    public async Task ReadPagesAsyncStampsEveryDocumentWithTheConfiguredTenant()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory.FullName, "doc-00.txt"), "content");

        var reader = new BoundedFileSystemSourceReader(_directory.FullName, "tenant-a", pageSize: 10);
        await foreach (IReadOnlyList<SourceDocument> page in reader.ReadPagesAsync())
        {
            Assert.All(page, document => Assert.Equal("tenant-a", document.TenantId));
        }
    }

    [Fact]
    public async Task ReadPagesAsyncPropagatesCancellation()
    {
        for (int i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_directory.FullName, $"doc-{i:D2}.txt"), $"content {i}");
        }

        var reader = new BoundedFileSystemSourceReader(_directory.FullName, "tenant-a", pageSize: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (IReadOnlyList<SourceDocument> _ in reader.ReadPagesAsync(cts.Token))
            {
            }
        });
    }

    [Fact]
    public void ConstructorRejectsNonPositivePageSize()
    {
        Assert.Throws<IngestionValidationException>(
            () => new BoundedFileSystemSourceReader(_directory.FullName, "tenant-a", pageSize: 0));
    }

    [Fact]
    public async Task ReadPagesAsyncReturnsNoPagesForAMissingDirectory()
    {
        var reader = new BoundedFileSystemSourceReader(
            Path.Combine(_directory.FullName, "does-not-exist"),
            "tenant-a");
        var pages = new List<IReadOnlyList<SourceDocument>>();
        await foreach (IReadOnlyList<SourceDocument> page in reader.ReadPagesAsync())
        {
            pages.Add(page);
        }

        Assert.Empty(pages);
    }
}
