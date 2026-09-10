# .NET TLS and network-access requirements

This document records the TLS and network-access guidance required by
[observability-security.md](../../spec/observability-security.md)'s "Require TLS-capable production connection
strings and document Atlas network-access requirements" for implementation-map
[slice 19](../../spec/implementation-map.md). This project does not implement its own TLS handling: every
provider/store accepts an `IMongoClient`/connection string and delegates entirely to `MongoDB.Driver`'s own
connection and TLS negotiation. This document is therefore deployment guidance, not a description of new code.

## Why this project has no TLS-specific code

Every constructor overload across `MongoDBMemoryProvider`, `MongoDBRAGProvider`, `MongoDBChatHistoryProvider`,
`MongoDBAgentSessionStore`, and `MongoDBCheckpointStore` accepts either an already-constructed
`IMongoClient`/`IMongoDatabase`/`IMongoCollection` (the recommended production path, letting the host application
own connection-string parsing and TLS configuration) or, for convenience overloads, a connection string that is
handed unmodified to `MongoClient`'s own constructor. Neither path adds, strips, or overrides TLS-related
connection-string options -- `MongoDB.Driver` alone is responsible for negotiating TLS, certificate validation,
and any client-certificate/mTLS configuration a connection string or `MongoClientSettings` requests.

## Production connection-string requirements

- Use `mongodb+srv://` (Atlas/DNS-seedlist) connection strings where available; these default to TLS enabled and
  do not require an explicit `tls=true` parameter.
- For non-SRV `mongodb://` connection strings against a TLS-required deployment (including every Atlas cluster),
  include `tls=true` (or the legacy `ssl=true` alias) explicitly. Do not rely on a deployment-side default for a
  production connection string; state the requirement in the connection string itself so a misconfigured
  non-TLS client fails to connect rather than silently connecting in the clear.
- Never embed credentials directly in a connection string checked into source control, CI configuration, or a
  sample's committed files. Every sample and integration test in this repository reads connection strings only
  from an environment variable (for example `MONGODB_URI`) documented in `dotnet/README.md`; this predates and is
  unchanged by this slice, and the [threat-model](dotnet-threat-model.md) and
  [telemetry](dotnet-telemetry.md) documents both confirm connection strings are never logged.
- When a deployment requires a client certificate (mTLS) or a custom certificate authority, configure it through
  `MongoClientSettings.SslSettings`/the connection string's `tlsCertificateKeyFile`/`tlsCAFile` options before
  constructing the `IMongoClient` this project's constructors accept; this project does not need, and does not
  provide, its own certificate-loading mechanism.

## Atlas network-access requirements

- An Atlas project's Network Access list (IP access list or, for AWS/Azure/GCP-hosted applications, VPC/Private
  Endpoint peering) must permit the application's egress network before any connection -- including this
  project's own credential-gated integration tests and samples -- can succeed. This is an Atlas project
  configuration step outside this project's code and is not automated by anything in this repository.
- Prefer a Private Endpoint or VPC peering connection over a public IP access list entry for production
  deployments, to keep MongoDB traffic off the public internet entirely; TLS remains required regardless of
  network path.
- Database users backing this project's runtime and provisioner roles (see
  [dotnet-least-privilege.md](dotnet-least-privilege.md)) should be scoped to the minimum built-in or custom role
  needed for their purpose and rotated according to the owning organization's credential-rotation policy; this
  project does not manage Atlas database users or Network Access list entries itself.

## What this project does verify

- No source file, log call, telemetry tag, or metric dimension in this project ever contains a connection string
  or credential -- verified by the sentinel-secret redaction tests described in
  [dotnet-telemetry.md](dotnet-telemetry.md) and by the repository-wide secret scan added in
  `.github/workflows/dotnet-security.yml`.
- Constructors that accept a connection string never log it, even at a diagnostic/debug level, and never include
  it in a thrown exception's message.
