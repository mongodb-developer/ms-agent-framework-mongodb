using System.Runtime.ExceptionServices;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// Runs a sample's primary body followed by one or more bounded cleanup steps (for example "delete this run's own
/// documents" and "drop the index this run created"), guaranteeing every cleanup step is attempted exactly once
/// regardless of whether the body or any other cleanup step failed, and never silently hiding a primary body
/// failure behind a later cleanup failure. Sample-local orchestration only; not part of MongoDB.AgentFramework's
/// public runtime API.
/// </summary>
public static class SampleCleanupOrchestration
{
    /// <summary>
    /// Runs <paramref name="body"/>, then always attempts every one of <paramref name="cleanupSteps"/> in order --
    /// each step runs even if an earlier step (or the body) failed, so for example an index-drop failure never
    /// prevents a document-delete attempt, and vice versa. If the body fails and every cleanup step succeeds, the
    /// original body exception is rethrown unmodified (same instance, original stack trace). If the body fails and
    /// one or more cleanup steps also fail, an <see cref="AggregateException"/> is thrown whose first inner
    /// exception is always the original body failure, followed by every cleanup failure in the order they
    /// occurred -- the primary failure is never hidden or discarded. If the body succeeds but one or more cleanup
    /// steps fail, either that single cleanup exception (if only one step failed) or an aggregate of all cleanup
    /// failures is thrown.
    /// </summary>
    public static async Task RunAsync(Func<Task> body, params Func<Task>[] cleanupSteps)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(cleanupSteps);

        Exception? primaryFailure = null;
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        List<Exception>? cleanupFailures = null;
        foreach (Func<Task> cleanup in cleanupSteps)
        {
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }
        }

        if (primaryFailure is not null)
        {
            if (cleanupFailures is { Count: > 0 })
            {
                throw new AggregateException(
                    "The primary operation failed and cleanup also failed. InnerExceptions[0] is the primary " +
                        "failure, which is never hidden; the remaining entries are cleanup failures.",
                    new[] { primaryFailure }.Concat(cleanupFailures));
            }

            // Rethrows the exact same exception instance with its original stack trace preserved, rather than
            // wrapping it, since no cleanup failure occurred that would need to be surfaced alongside it.
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        else if (cleanupFailures is { Count: > 0 })
        {
            if (cleanupFailures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
            }

            throw new AggregateException("One or more cleanup steps failed.", cleanupFailures);
        }
    }
}
