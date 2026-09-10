namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// Wraps a value already known to be a validated, independent snapshot -- for example an options object produced
/// by a single call to its owning type's own copy/validate logic -- so a constructor overload that accepts it can
/// be statically distinguished from one that accepts raw, not-yet-validated caller input. A "core" constructor
/// reached only after that snapshot already exists can then accept this wrapper instead of re-running
/// validation/copy logic a second time.
///
/// This matters specifically for a connection-string-owned-client constructor family: if the same
/// validation/copy logic ran again after the owned client already existed, and it ever behaved differently on a
/// second pass (for example enumerating a caller-controlled <see cref="System.Collections.Generic.IEnumerable{T}"/>
/// a second time) or threw for any other reason, the just-created client would leak, since no instance would ever
/// exist to dispose it. Validating/copying exactly once, entirely before the client is created, and threading the
/// resulting snapshot through this wrapper for the rest of construction avoids that. This type carries no behavior
/// of its own.
/// </summary>
internal readonly record struct ValidatedOptions<T>(T Value);
