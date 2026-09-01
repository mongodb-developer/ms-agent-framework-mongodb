using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

public sealed class GeneratedIndexProvisionerTests
{
    [Fact]
    public async Task ProvisionAsyncCreatesAndOwnsWhenTheGeneratedNameDoesNotAlreadyExist()
    {
        int ensureCalls = 0;
        int validateCalls = 0;
        var provisioner = new GeneratedIndexProvisioner(
            existsAsync: _ => Task.FromResult(false),
            ensureAsync: _ => { ensureCalls++; return Task.CompletedTask; },
            validateAsync: _ => { validateCalls++; return Task.CompletedTask; });

        await provisioner.ProvisionAsync();

        Assert.True(provisioner.CreatedByThisRun);
        Assert.Equal(1, ensureCalls);
        Assert.Equal(0, validateCalls);
    }

    [Fact]
    public async Task ProvisionAsyncValidatesRatherThanCreatesWhenTheGeneratedNameAlreadyExists()
    {
        int ensureCalls = 0;
        int validateCalls = 0;
        var provisioner = new GeneratedIndexProvisioner(
            existsAsync: _ => Task.FromResult(true),
            ensureAsync: _ => { ensureCalls++; return Task.CompletedTask; },
            validateAsync: _ => { validateCalls++; return Task.CompletedTask; });

        await provisioner.ProvisionAsync();

        Assert.False(provisioner.CreatedByThisRun);
        Assert.Equal(0, ensureCalls);
        Assert.Equal(1, validateCalls);
    }

    [Fact]
    public async Task ProvisionAsyncRecordsOwnershipBeforeEnsureEvenWhenEnsureCreatesThenTimesOut()
    {
        // Simulates the real MongoDBRAGIndexManager.EnsureVectorSearchIndexAsync(waitUntilReady: true) failure
        // mode: the index genuinely gets created, but the bounded wait for it to become READY times out and the
        // call throws. Ownership must already be recorded as true *before* this throw, not only after a
        // successful return, so a caller's cleanup step still knows to attempt dropping the index it started
        // creating rather than leaking it forever.
        var provisioner = new GeneratedIndexProvisioner(
            existsAsync: _ => Task.FromResult(false),
            ensureAsync: _ => throw new TimeoutException("Simulated: index created but wait-until-ready timed out."),
            validateAsync: _ => Task.CompletedTask);

        await Assert.ThrowsAsync<TimeoutException>(() => provisioner.ProvisionAsync());

        Assert.True(provisioner.CreatedByThisRun);
    }

    [Fact]
    public async Task ProvisionAsyncLeavesOwnershipFalseWhenExistsCheckItselfThrows()
    {
        var provisioner = new GeneratedIndexProvisioner(
            existsAsync: _ => throw new InvalidOperationException("Simulated existence-check failure."),
            ensureAsync: _ => Task.CompletedTask,
            validateAsync: _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.ProvisionAsync());

        Assert.False(provisioner.CreatedByThisRun);
    }

    [Fact]
    public async Task ProvisionAsyncPropagatesCancellationToEachDelegate()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken? observedExists = null;
        var provisioner = new GeneratedIndexProvisioner(
            existsAsync: ct => { observedExists = ct; return Task.FromResult(false); },
            ensureAsync: _ => Task.CompletedTask,
            validateAsync: _ => Task.CompletedTask);

        await provisioner.ProvisionAsync(cts.Token);

        Assert.Equal(cts.Token, observedExists);
    }

    [Fact]
    public void ConstructorRejectsNullDelegates()
    {
        Assert.Throws<ArgumentNullException>(() => new GeneratedIndexProvisioner(
            null!, _ => Task.CompletedTask, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new GeneratedIndexProvisioner(
            _ => Task.FromResult(false), null!, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new GeneratedIndexProvisioner(
            _ => Task.FromResult(false), _ => Task.CompletedTask, null!));
    }
}
