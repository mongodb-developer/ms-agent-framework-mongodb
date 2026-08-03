using MongoDB.AgentFramework.Internal.IndexManagement;

namespace MongoDB.AgentFramework.Tests.Internal.IndexManagement;

/// <summary>
/// Public-seam tests for <see cref="BoundedExponentialPolling.RunAsync{T}"/>: bounded exponential backoff,
/// non-transient fail-fast, a stable last-transient-exception as timeout context, and cancellation semantics that
/// distinguish the caller's own token from the per-attempt/overall deadline (docs/spec/features/index-management.md).
/// </summary>
public sealed class BoundedExponentialPollingTests
{
    private sealed class MarkerException(string message) : Exception(message);

    [Fact]
    public async Task RunAsync_returns_immediately_when_the_first_attempt_succeeds()
    {
        int attempts = 0;
        int result = await BoundedExponentialPolling.RunAsync(
            _ => { attempts++; return Task.FromResult(42); },
            static _ => true,
            static exception => exception,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RunAsync_retries_a_transient_failure_until_it_succeeds()
    {
        int attempts = 0;
        int result = await BoundedExponentialPolling.RunAsync(
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? throw new MarkerException("not ready yet")
                    : Task.FromResult(99);
            },
            static exception => exception is MarkerException,
            static exception => exception,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.Equal(99, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RunAsync_rethrows_a_non_transient_failure_immediately_without_retrying()
    {
        int attempts = 0;

        await Assert.ThrowsAsync<MarkerException>(() => BoundedExponentialPolling.RunAsync<int>(
            _ => { attempts++; throw new MarkerException("actionable, not transient"); },
            static _ => false,
            static exception => exception,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RunAsync_calls_onTimeout_with_the_last_transient_exception_when_the_deadline_elapses()
    {
        var lastException = new MarkerException("still not ready");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BoundedExponentialPolling.RunAsync<int>(
                _ => throw lastException,
                static exception => exception is MarkerException,
                exception => new InvalidOperationException("bounded timeout", exception),
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None));

        // The stable, actual last transient failure is preserved as context rather than being replaced by a
        // generic TimeoutException once the deadline elapses.
        Assert.Same(lastException, exception.InnerException);
    }

    [Fact]
    public async Task RunAsync_propagates_caller_cancellation_immediately_distinct_from_a_timeout()
    {
        using var cancellation = new CancellationTokenSource();
        bool onTimeoutCalled = false;

        Task<int> task = BoundedExponentialPolling.RunAsync<int>(
            async token =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return 0;
            },
            static exception => exception is MarkerException,
            exception =>
            {
                onTimeoutCalled = true;
                return new InvalidOperationException("should not be reached", exception);
            },
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(50),
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(onTimeoutCalled);
    }

    [Fact]
    public async Task RunAsync_bounds_a_hung_attempt_by_the_remaining_overall_deadline()
    {
        // The attempt only observes the per-attempt/deadline-linked token it is given (never the caller's own
        // token directly, and never returning on its own), simulating a MongoDB call that never itself notices
        // cancellation promptly; RunAsync must still bound the overall wait to roughly `timeout`.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BoundedExponentialPolling.RunAsync<int>(
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    return 0;
                },
                static _ => false,
                exception => new InvalidOperationException("bounded timeout", exception),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        stopwatch.Stop();
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected the hung attempt to be bounded by the deadline, but it took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_rejects_a_non_positive_timeout_configuration()
    {
        await Assert.ThrowsAsync<MongoDBConfigurationException>(() => BoundedExponentialPolling.RunAsync<int>(
            static _ => Task.FromResult(0),
            static _ => true,
            static exception => exception,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None));
    }
}
