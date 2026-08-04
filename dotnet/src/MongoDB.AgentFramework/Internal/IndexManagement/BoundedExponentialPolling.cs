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
    /// <paramref name="maxInterval"/> and never made to exceed the remaining time before the deadline. Each
    /// attempt receives a token linking <paramref name="cancellationToken"/> with a per-attempt deadline set to
    /// the remaining overall budget, so a single hung MongoDB call can never keep this loop (or the underlying
    /// request) alive past <paramref name="timeout"/> even if that individual call never itself observes
    /// cancellation promptly. A cancellation caused by <paramref name="cancellationToken"/> itself always
    /// propagates immediately as <see cref="OperationCanceledException"/>, distinct from a cancellation caused
    /// only by the per-attempt deadline, which is instead treated as a bounded-timeout condition (via
    /// <paramref name="onTimeout"/>), exactly like a transient exception still failing at the deadline.
    /// </summary>
    /// <param name="attempt">The operation to retry, given a token that is cancelled at the per-attempt deadline or by <paramref name="cancellationToken"/>.</param>
    /// <param name="isTransient">
    /// Decides whether a thrown exception should be retried. Returning <see langword="false"/> for a given
    /// exception rethrows it immediately without waiting for the deadline.
    /// </param>
    /// <param name="onTimeout">
    /// Builds the exception raised when the deadline elapses while the last attempt's failure is still
    /// transient (or the last attempt was still running when its per-attempt deadline fired), receiving a
    /// <see cref="TimeoutException"/> as context (for example as an inner exception).
    /// </param>
    /// <param name="timeout">The total bounded deadline, starting from the first call.</param>
    /// <param name="initialInterval">The delay before the first retry.</param>
    /// <param name="maxInterval">The maximum delay between retries after exponential growth.</param>
    /// <param name="cancellationToken">A token checked before every attempt and delay, and linked into every per-attempt token.</param>
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
        Exception? lastTransientException = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remainingForAttempt = timeout - elapsed.Elapsed;
            if (remainingForAttempt <= TimeSpan.Zero)
            {
                throw onTimeout(lastTransientException ?? new TimeoutException(
                    $"The operation exceeded its {timeout} deadline."));
            }

            var attemptDeadline = new CancellationTokenSource(remainingForAttempt);
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, attemptDeadline.Token);

            // Merely handing attempt a token that will be cancelled is not itself a bound: an attempt that never
            // observes its token (an "uncooperative" hung call) would otherwise keep this loop -- and the
            // underlying request -- alive forever. Racing the attempt against an unbounded delay tied to the
            // same linked token guarantees this loop always moves on at the per-attempt deadline (or caller
            // cancellation) even when attempt() itself never returns. A synchronous throw from attempt is
            // normalized into a faulted Task so it is still routed through the same catch clauses as an
            // asynchronous failure below.
            Task<T> attemptTask;
            try
            {
                attemptTask = attempt(linked.Token);
            }
            catch (Exception exception)
            {
                attemptTask = Task.FromException<T>(exception);
            }

            Task unboundedDelay = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            Task winner = await Task.WhenAny(attemptTask, unboundedDelay).ConfigureAwait(false);

            if (winner == attemptTask)
            {
                // The attempt itself completed (successfully, faulted, or self-cancelled) before the linked
                // token was forced to intervene; inspect the outcome exactly as before the race was introduced.
                // Neither attemptDeadline nor linked is disposed here: unboundedDelay below may still be pending
                // against the same linked token, and disposing a CancellationTokenSource out from under a
                // Task.Delay still racing against it is unsafe. Both sources' own timers self-resolve within the
                // already-bounded remainingForAttempt window regardless, so relying on the finalizer is deliberate.
                bool deadlineFired = attemptDeadline.IsCancellationRequested;
                try
                {
                    return await attemptTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The caller's own token was cancelled, not merely the per-attempt deadline: always
                    // propagate this immediately, regardless of isTransient.
                    throw;
                }
                catch (OperationCanceledException) when (deadlineFired)
                {
                    // The individual attempt outlived the remaining overall budget (for example a hung MongoDB
                    // call that observed cancellation, just not promptly). This consumed the entire remaining
                    // budget, so it is always treated as the bounded timeout having elapsed, never retried again.
                    throw onTimeout(lastTransientException ?? new TimeoutException(
                        $"The operation exceeded its {timeout} deadline while the last attempt was still in progress."));
                }
                catch (Exception exception) when (isTransient(exception))
                {
                    lastTransientException = exception;
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
            else
            {
                // The linked token was cancelled (caller cancellation or the per-attempt deadline) while
                // attempt() was still running: it is abandoned here -- never awaited again -- but its eventual
                // completion (including any fault) must still be safely observed so it never surfaces as an
                // unobserved task exception. Neither per-attempt token source is disposed for the same reason as
                // the attempt-wins branch above.
                _ = attemptTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw onTimeout(lastTransientException ?? new TimeoutException(
                    $"The operation exceeded its {timeout} deadline while the last attempt was still in progress."));
            }
        }
    }
}
