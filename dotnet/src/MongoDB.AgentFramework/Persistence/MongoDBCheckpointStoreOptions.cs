namespace MongoDB.AgentFramework;

/// <summary>
/// Immutable authorization scope and TTL/deadline options for <see cref="MongoDBCheckpointStore"/>. One
/// instance scopes every operation to exactly one workflow definition (and, if configured, one tenant); the
/// <c>sessionId</c> parameter threaded through every <see cref="MongoDBCheckpointStore"/> method is the
/// workflow run/session partition within that scope.
/// </summary>
public sealed record MongoDBCheckpointStoreOptions
{
    /// <summary>The minimum required length, in bytes, of <see cref="ContinuationTokenSigningKey"/>.</summary>
    public const int MinimumContinuationTokenSigningKeyLength = 32;

    /// <summary>Gets the optional tenant isolation identifier.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets the required workflow definition identifier.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>
    /// Gets the required, server-held secret key used to sign (HMAC-SHA256) and validate pagination
    /// continuation tokens. Must be at least <see cref="MinimumContinuationTokenSigningKeyLength"/>
    /// cryptographically random bytes (for example <c>RandomNumberGenerator.GetBytes(32)</c>), configured once
    /// and kept stable and identical across every <see cref="MongoDBCheckpointStore"/> instance that must accept
    /// each other's tokens (for example every replica of a horizontally scaled service) -- rotating it
    /// invalidates every token issued under the previous key. This key is the sole secret behind token
    /// validation: it is never derived from, or discoverable from, the token's own contents. Load it from a
    /// secret manager or a protected environment variable, never a source-controlled literal. This value is
    /// deliberately excluded from this record's <see cref="ToString"/> so it is never accidentally logged.
    /// </summary>
    public required byte[] ContinuationTokenSigningKey { get; init; }

    /// <summary>
    /// Gets the default TTL applied when a caller does not pass an explicit <c>expiresAt</c> to
    /// <see cref="MongoDBCheckpointStore.SaveCheckpointAsync"/> or when the framework's own
    /// <see cref="MongoDBCheckpointStore.CreateCheckpointAsync"/> hook is invoked (which accepts no expiry
    /// parameter at all). Checkpoints written without any expiration (neither this default nor an explicit
    /// value) never expire. Expiring a checkpoint that is a lineage parent of a still-live checkpoint leaves a
    /// lineage gap; see docs/spec/features/persistence.md.
    /// </summary>
    public TimeSpan? DefaultExpiration { get; init; }

    /// <summary>Gets the optional complete retrieval/list deadline.</summary>
    public TimeSpan? RetrievalTimeout { get; init; }

    /// <summary>Gets the optional complete save/delete deadline.</summary>
    public TimeSpan? PersistenceTimeout { get; init; }

    /// <summary>Validates configuration without contacting MongoDB.</summary>
    public void Validate()
    {
        RequireText(WorkflowId, nameof(WorkflowId));
        if (TenantId is not null)
        {
            RequireText(TenantId, nameof(TenantId));
        }

        if (ContinuationTokenSigningKey is null ||
            ContinuationTokenSigningKey.Length < MinimumContinuationTokenSigningKeyLength)
        {
            throw new MongoDBConfigurationException(
                $"{nameof(ContinuationTokenSigningKey)} must be at least " +
                $"{MinimumContinuationTokenSigningKeyLength} cryptographically random bytes. Pagination cannot " +
                "operate securely without a real server-held secret; generate one with " +
                "RandomNumberGenerator.GetBytes and configure it, kept stable, from a secret manager or " +
                "protected environment variable.");
        }

        ValidateDuration(DefaultExpiration, nameof(DefaultExpiration));
        ValidateDuration(RetrievalTimeout, nameof(RetrievalTimeout));
        ValidateDuration(PersistenceTimeout, nameof(PersistenceTimeout));
    }

    /// <summary>Redacts <see cref="ContinuationTokenSigningKey"/> so it is never accidentally logged or displayed.</summary>
    public override string ToString() =>
        $"{nameof(MongoDBCheckpointStoreOptions)} {{ TenantId = {TenantId ?? "<null>"}, WorkflowId = {WorkflowId}, " +
        $"ContinuationTokenSigningKey = <redacted>, DefaultExpiration = {DefaultExpiration}, " +
        $"RetrievalTimeout = {RetrievalTimeout}, PersistenceTimeout = {PersistenceTimeout} }}";

    internal static string RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MongoDBConfigurationException($"{name} must not be empty.");
        }

        return value;
    }

    private static void ValidateDuration(TimeSpan? value, string name)
    {
        if (value is { } duration && duration <= TimeSpan.Zero)
        {
            throw new MongoDBConfigurationException($"{name} must be positive when configured.");
        }
    }
}
