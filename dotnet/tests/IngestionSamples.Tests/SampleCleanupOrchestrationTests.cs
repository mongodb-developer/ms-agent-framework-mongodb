using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class SampleCleanupOrchestrationTests
{
    [Fact]
    public async Task RunAsyncRunsEveryCleanupStepWhenTheBodySucceeds()
    {
        var invoked = new List<int>();

        await SampleCleanupOrchestration.RunAsync(
            body: () => Task.CompletedTask,
            () => { invoked.Add(1); return Task.CompletedTask; },
            () => { invoked.Add(2); return Task.CompletedTask; });

        Assert.Equal([1, 2], invoked);
    }

    [Fact]
    public async Task RunAsyncAttemptsEveryCleanupStepEvenWhenAnEarlierOneThrows()
    {
        var invoked = new List<int>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SampleCleanupOrchestration.RunAsync(
            body: () => Task.CompletedTask,
            () => { invoked.Add(1); throw new InvalidOperationException("cleanup step 1 failed"); },
            () => { invoked.Add(2); return Task.CompletedTask; }));

        // Both cleanup steps (e.g. "delete documents" and "drop index") must always be attempted -- the failure
        // of one must never prevent the other from being attempted.
        Assert.Equal([1, 2], invoked);
    }

    [Fact]
    public async Task RunAsyncPreservesTheOriginalPrimaryFailureWhenAllCleanupStepsSucceed()
    {
        var primary = new InvalidOperationException("body failed");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SampleCleanupOrchestration.RunAsync(
                body: () => throw primary,
                () => Task.CompletedTask,
                () => Task.CompletedTask));

        // The exact original exception instance (with its original stack trace) propagates, unmodified, when
        // cleanup does not itself fail -- no wrapping should occur when it is not needed.
        Assert.Same(primary, thrown);
    }

    [Fact]
    public async Task RunAsyncAggregatesTheBodyFailureAndCleanupFailuresWithoutHidingThePrimaryFailure()
    {
        var primary = new InvalidOperationException("body failed");
        var cleanupFailure = new TimeoutException("index drop timed out");

        AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(
            () => SampleCleanupOrchestration.RunAsync(
                body: () => throw primary,
                () => Task.CompletedTask,
                () => throw cleanupFailure));

        // The primary body failure must never be silently swallowed by a later cleanup failure: both must be
        // observable, with the primary failure surfaced first.
        Assert.Same(primary, thrown.InnerExceptions[0]);
        Assert.Contains(cleanupFailure, thrown.InnerExceptions);
    }

    [Fact]
    public async Task RunAsyncThrowsTheSingleCleanupFailureWhenTheBodySucceedsButOneCleanupStepFails()
    {
        var cleanupFailure = new TimeoutException("index drop timed out");

        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => SampleCleanupOrchestration.RunAsync(
                body: () => Task.CompletedTask,
                () => throw cleanupFailure));

        Assert.Same(cleanupFailure, thrown);
    }

    [Fact]
    public async Task RunAsyncAggregatesMultipleCleanupFailuresWhenTheBodySucceeds()
    {
        var firstFailure = new InvalidOperationException("documents cleanup failed");
        var secondFailure = new TimeoutException("index drop timed out");

        AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(
            () => SampleCleanupOrchestration.RunAsync(
                body: () => Task.CompletedTask,
                () => throw firstFailure,
                () => throw secondFailure));

        Assert.Equal([firstFailure, secondFailure], thrown.InnerExceptions);
    }

    [Fact]
    public async Task RunAsyncRejectsNullBodyOrCleanupSteps()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => SampleCleanupOrchestration.RunAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SampleCleanupOrchestration.RunAsync(() => Task.CompletedTask, cleanupSteps: null!));
    }
}
