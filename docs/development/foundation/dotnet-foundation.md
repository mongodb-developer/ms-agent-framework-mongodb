# .NET foundation and shared internals

This implementation realizes slice 1 of the
[implementation map](../../spec/implementation-map.md) for the .NET package. It
follows the ownership and public-contract decisions in
[ADR 0003](../../decisions/0003-integrate-through-public-agent-framework-contracts.md),
[ADR 0004](../../decisions/0004-publish-independent-language-packages.md), and
[ADR 0005](../../decisions/0005-fix-resource-ownership-at-construction.md).

## Package

The solution is `dotnet/MongoDB.AgentFramework.slnx`. The package project is
`dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj`, targets
`net8.0`, `net9.0`, and `net10.0`, and publishes as
`MongoDB.AgentFramework`.

Dependency ranges are bounded to one major version. Publication remains blocked
until the compatibility matrix verifies the oldest and newest advertised
versions as required by [ADR 0014](../../decisions/0014-publish-only-tested-compatibility-ranges.md).

## Shared mechanics

- `OwnedResource<T>` fixes ownership at construction and disposes an owned
  resource at most once. Borrowed clients, databases, collections, and embedding
  generators remain caller-owned.
- `MongoClientFactory` validates connection strings before constructing a client,
  records created clients as owned, and records injected clients as borrowed.
- `FieldPath` rejects null bytes, empty segments, MongoDB operator segments,
  positional array syntax, and reserved aliases before I/O. Nested BSON lookup
  fails with a stable mapping exception.
- `EmbeddingValidator` checks positive dimensions, result counts, vector lengths,
  and finite values.
- `CapabilityResult` is immutable, copies detected metadata, and requires
  remediation for unsupported capabilities.
- Public exceptions under `MongoDB.AgentFramework` provide stable configuration,
  embedding, capability, index, mapping, retrieval, and persistence categories
  while preserving inner exceptions.

Feature modules may depend on these internals. The internals do not depend on a
feature module.

## Verification

Offline unit tests cover ownership, repeated disposal, field-path safety, BSON
resolution, embedding count/dimensions/finite values, capability remediation,
and connection-string error preservation. The package must restore, build all
target frameworks, pass tests, and produce a NuGet package before this slice is
merged.
