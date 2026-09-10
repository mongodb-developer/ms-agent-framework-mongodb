using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class DeterministicIdTests
{
    [Fact]
    public void ForChunkIsStableAcrossRepeatedCalls()
    {
        string first = DeterministicId.ForChunk("tenant-a", "source-1", 0);
        string second = DeterministicId.ForChunk("tenant-a", "source-1", 0);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ForChunkDiffersByChunkIndex()
    {
        string chunk0 = DeterministicId.ForChunk("tenant-a", "source-1", 0);
        string chunk1 = DeterministicId.ForChunk("tenant-a", "source-1", 1);
        Assert.NotEqual(chunk0, chunk1);
    }

    [Fact]
    public void ForChunkDiffersByTenant()
    {
        string tenantA = DeterministicId.ForChunk("tenant-a", "source-1", 0);
        string tenantB = DeterministicId.ForChunk("tenant-b", "source-1", 0);
        Assert.NotEqual(tenantA, tenantB);
    }

    [Fact]
    public void ForChunkDiffersBySourceId()
    {
        string sourceOne = DeterministicId.ForChunk("tenant-a", "source-1", 0);
        string sourceTwo = DeterministicId.ForChunk("tenant-a", "source-2", 0);
        Assert.NotEqual(sourceOne, sourceTwo);
    }

    [Fact]
    public void ForParentIsStableAndDistinctFromChunkIds()
    {
        string parentFirst = DeterministicId.ForParent("tenant-a", "source-1");
        string parentSecond = DeterministicId.ForParent("tenant-a", "source-1");
        Assert.Equal(parentFirst, parentSecond);

        string chunkId = DeterministicId.ForChunk("tenant-a", "source-1", 0);
        Assert.NotEqual(parentFirst, chunkId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForChunkRejectsEmptyTenantId(string? tenantId)
    {
        Assert.Throws<IngestionValidationException>(() => DeterministicId.ForChunk(tenantId!, "source-1", 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ForChunkRejectsEmptySourceId(string? sourceId)
    {
        Assert.Throws<IngestionValidationException>(() => DeterministicId.ForChunk("tenant-a", sourceId!, 0));
    }

    [Fact]
    public void ForChunkRejectsNegativeIndex()
    {
        Assert.Throws<IngestionValidationException>(() => DeterministicId.ForChunk("tenant-a", "source-1", -1));
    }

    [Fact]
    public void ForChunkDoesNotCollideAcrossControlDelimiterFieldBoundaryShifts()
    {
        // "tenant-a\u001fb" + "c" and "tenant-a" + "b\u001fc" would concatenate to the exact same delimiter-joined
        // string ("tenant-a\u001fb\u001fc"), so a naive `string.Join('\u001f', ...)`-style preimage would collide
        // these two logically distinct (tenantId, sourceId) tuples into the same hash/ID. Canonical length-prefixed
        // framing must keep them distinct.
        string first = DeterministicId.ForChunk("tenant-a\u001fb", "c", 0);
        string second = DeterministicId.ForChunk("tenant-a", "b\u001fc", 0);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForParentDoesNotCollideAcrossControlDelimiterFieldBoundaryShifts()
    {
        string first = DeterministicId.ForParent("tenant-a\u001fb", "c");
        string second = DeterministicId.ForParent("tenant-a", "b\u001fc");

        Assert.NotEqual(first, second);
    }
}
