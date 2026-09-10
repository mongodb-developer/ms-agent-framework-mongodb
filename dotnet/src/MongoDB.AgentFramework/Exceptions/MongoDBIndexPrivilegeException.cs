namespace MongoDB.AgentFramework;

/// <summary>
/// Raised when a MongoDB Search/Vector Search index operation fails because the connected identity lacks the
/// required privilege, distinguished from <see cref="MongoDBIndexMissingException"/>,
/// <see cref="MongoDBIndexMismatchException"/>, and <see cref="MongoDBIndexNotReadyException"/> so callers and
/// operators can tell an authorization gap apart from a definition or readiness problem. See
/// docs/spec/features/index-management.md's least-privilege table and
/// docs/development/index-management/dotnet-index-management.md for the exact operation categories that require
/// elevated (provisioner) privileges versus the reduced set required by runtime identities.
/// </summary>
public sealed class MongoDBIndexPrivilegeException : MongoDBIndexException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBIndexPrivilegeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBIndexPrivilegeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
