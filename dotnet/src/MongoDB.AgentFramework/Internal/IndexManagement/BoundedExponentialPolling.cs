using System.Diagnostics;

namespace MongoDB.AgentFramework.Internal.IndexManagement;

/// <summary>
/// A bounded exponential-backoff retry loop shared by every index-readiness polling path (docs/spec/features/
/// index-management.md: "use a monotonic deadline", "use a bounded interval", "support cancellation on every
/// request and delay"). Callers decide which failures should be retried (for example "not ready yet") versus
/// rethrown immediately (for example a definition mismatch, which polling can never resolve), so a definitively
/// wrong outcome fails fast instead of being retried until the deadline.
/// </summary>
internal static class BoundedExponentialPolling
{
    /// <summary>
    /// Repeatedly invokes <paramref name="attempt"/> until it completes without throwing, the monotonic
    /// <paramref name="timeout"/> deadline elapses, or <paramref name="cancellationToken"/> is cancelled. The
    /// delay between attempts doubles after each retry starting from <paramref name="initialInterval"/>, capped at
    /// <paramref name="maxInterval"/> and never made to exceed the remaining time before the deadline.
    /// <see cref="OperationCanceledException"/> is never treated as transient and always propagates immediately,
    /// regardless of <paramref name="isTransient"/>.
    /// </summary>
    /// <param name="attempt">The operation to retry.</param>
    /// <param name="isTransient">
    /// Decides whether a thrown exception should be retried. Returning <see langword="false"/> for a given
    /// exception rethrows it immediately without waiting for the deadline.
    /// </param>
    /// <param name="onTimeout">
    /// Builds the exception raised when the deadline elapses while the last attempt's failure is still
    /// transient, receiving that last exception as context (for example as an inner exception).
    /// </param>
    /// <param name="timeout">The total bounded deadline, starting from the first call.</param>
    /// <param name="initialInterval">The delay before the first retry.</param>
    /// <param name="maxInterval">The maximum delay between retries after exponential growth.</param>
    /// <param name="cancellationToken">A token checked before every attempt and delay.</param>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> attempt,
        Func<Exception, bool> isTransient,
        Func<Exception, Exception> onTimeout,
        TimeSpan timeout,
        TimeSpan initialInterval,
        TimeSpan maxInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(isTransient);
        ArgumentNullException.ThrowIfNull(onTimeout);
        if (timeout <= TimeSpan.Zero || initialInterval <= TimeSpan.Zero || maxInterval <= TimeSpan.Zero)
        {
            throw new MongoDBConfigurationException(
                "timeout, initialInterval, and maxInterval must all be positive.");
        }

        var elapsed = Stopwatch.StartNew();
        TimeSpan delay = initialInterval < maxInterval ? initialInterval : maxInterval;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await attempt(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && isTransient(exception))
            {
                TimeSpan remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw onTimeout(exception);
                }

                TimeSpan wait = delay < remaining ? delay : remaining;
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                TimeSpan doubled = delay + delay;
                delay = doubled < maxInterval ? doubled : maxInterval;
            }
        }
    }
}
