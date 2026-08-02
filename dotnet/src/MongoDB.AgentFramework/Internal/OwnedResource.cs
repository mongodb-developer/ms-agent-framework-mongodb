using System.Threading;

namespace MongoDB.AgentFramework.Internal;

internal sealed class OwnedResource<T> : IAsyncDisposable
    where T : class
{
    private readonly Action<T>? _dispose;
    private int _disposed;

    private OwnedResource(T value, bool ownsValue, Action<T>? dispose)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        OwnsValue = ownsValue;
        _dispose = ownsValue ? dispose ?? throw new ArgumentNullException(nameof(dispose)) : null;
    }

    public T Value { get; }

    public bool OwnsValue { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static OwnedResource<T> Owned(T value, Action<T> dispose) =>
        new(value, ownsValue: true, dispose);

    public static OwnedResource<T> Borrowed(T value) =>
        new(value, ownsValue: false, dispose: null);

    public ValueTask DisposeAsync()
    {
        if (OwnsValue && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose!(Value);
        }

        return ValueTask.CompletedTask;
    }
}
