namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// Establishes "this run created the index" ownership <em>before</em> attempting to create it, not only after
/// creation succeeds -- so a later failure while ensuring the index (for example, the index genuinely gets
/// created but the bounded wait for it to become READY times out) still leaves <see cref="CreatedByThisRun"/>
/// correctly recorded as <see langword="true"/>, and a caller's cleanup step will still attempt to drop the index
/// it started creating rather than silently leaking it. Delegate-based so it is testable with fakes without a
/// live MongoDB deployment or a concrete <c>MongoDBRAGIndexManager</c> (which is sealed and has no interface).
/// Sample-local orchestration only; not part of MongoDB.AgentFramework's public runtime API.
/// </summary>
public sealed class GeneratedIndexProvisioner
{
    private readonly Func<CancellationToken, Task<bool>> _existsAsync;
    private readonly Func<CancellationToken, Task> _ensureAsync;
    private readonly Func<CancellationToken, Task> _validateAsync;

    /// <summary>
    /// Initializes a provisioner over caller-supplied existence-check, ensure (create + optionally wait until
    /// ready), and validate delegates -- typically thin wrappers around a single
    /// <c>MongoDBRAGIndexManager</c>'s <c>GetVectorSearchIndexAsync</c>/<c>EnsureVectorSearchIndexAsync</c>/
    /// <c>ValidateVectorSearchIndexAsync</c> methods for one generated, sample-owned index name.
    /// </summary>
    public GeneratedIndexProvisioner(
        Func<CancellationToken, Task<bool>> existsAsync,
        Func<CancellationToken, Task> ensureAsync,
        Func<CancellationToken, Task> validateAsync)
    {
        _existsAsync = existsAsync ?? throw new ArgumentNullException(nameof(existsAsync));
        _ensureAsync = ensureAsync ?? throw new ArgumentNullException(nameof(ensureAsync));
        _validateAsync = validateAsync ?? throw new ArgumentNullException(nameof(validateAsync));
    }

    /// <summary>
    /// <see langword="true"/> once this run has determined it owns the generated index's lifecycle -- set
    /// <em>before</em> the create/ensure attempt is even made, not only after that attempt succeeds, so a
    /// subsequent throw from the ensure delegate still leaves this correctly <see langword="true"/> rather than
    /// leaving cleanup unaware that an index may have started being created.
    /// </summary>
    public bool CreatedByThisRun { get; private set; }

    /// <summary>
    /// Checks whether the generated index name already exists. If absent (the expected case, since the name is
    /// always freshly generated), records ownership by intent and only then creates/ensures it. If already
    /// present (the near-impossible generated-name collision case), validates it instead and leaves ownership
    /// <see langword="false"/>, since this run did not create it and must never drop it.
    /// </summary>
    public async Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        if (await _existsAsync(cancellationToken).ConfigureAwait(false))
        {
            CreatedByThisRun = false;
            await _validateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Ownership is recorded here -- before the ensure delegate is invoked -- specifically so that if the
        // ensure delegate throws partway through (e.g. index creation succeeded but the bounded wait for READY
        // timed out), CreatedByThisRun is already true and a caller's cleanup step still knows to attempt
        // dropping the index.
        CreatedByThisRun = true;
        await _ensureAsync(cancellationToken).ConfigureAwait(false);
    }
}
