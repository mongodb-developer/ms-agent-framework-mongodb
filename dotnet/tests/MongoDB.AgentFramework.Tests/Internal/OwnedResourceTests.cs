using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework.Tests.Internal;

public sealed class OwnedResourceTests
{
    [Fact]
    public async Task Owned_resource_is_disposed_exactly_once()
    {
        var resource = new object();
        int disposeCount = 0;
        var handle = OwnedResource<object>.Owned(resource, _ => disposeCount++);

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        Assert.True(handle.OwnsValue);
        Assert.True(handle.IsDisposed);
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public async Task Borrowed_resource_is_never_disposed()
    {
        var handle = OwnedResource<object>.Borrowed(new object());

        await handle.DisposeAsync();

        Assert.False(handle.OwnsValue);
        Assert.False(handle.IsDisposed);
    }
}
