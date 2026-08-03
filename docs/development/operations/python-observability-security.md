# Python observability and security

This document describes implementation-map slice 19 for the already integrated Python
features. The normative requirements are
[observability and security](../../spec/observability-security.md),
[resilience](../../spec/resilience.md), and [testing](../../spec/testing.md). The design
rationale is in ADRs [0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md),
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md), and
[0017](../../decisions/0017-use-standard-telemetry-without-unapproved-markers.md).

## Implementation

`agent_framework_mongodb._shared.observability` is inward-only. Its `instrument`
decorator wraps public async operations with standard Python logging and the public
OpenTelemetry API already supplied by Agent Framework Core. The package installs no
exporter and does not configure a tracer provider.

Completion logs use the `agent_framework_mongodb` logger. Spans are named
`agent_framework_mongodb.<feature>.<operation>`. The shared allowlist is:

| Field | Values |
| --- | --- |
| `feature` | `memory`, `history`, `rag`, `indexing`, `session_store`, `checkpoint_store` |
| `operation` | A bounded operation such as `retrieve`, `persist`, `delete`, `load`, `list`, `validate_index`, or `ensure_index` |
| `mode` | RAG only: `ann`, `enn`, `full_text`, or `hybrid_rrf` |
| `outcome` | `success`, `empty`, `failed`, or `cancelled` |
| `result_count` | Non-negative operation result count |
| `error_category` | Stable category, never an exception message |
| `duration_ms` | Monotonic elapsed duration |

The implementation deliberately does not call OpenTelemetry exception-recording
helpers because driver exception messages can contain deployment details. Connection
strings, credentials, embeddings, query or message text, retrieved content, filters,
scope values, BSON, database/collection/host names, document IDs, source URLs, and
driver messages are not log fields or span attributes. Index names are also omitted
pending a separate redaction approval.

`agent_framework_mongodb._shared.error_handling` classifies PyMongo failures using
exception types, numeric codes, code names, and retry labels only. It never parses or
logs the driver message. The original PyMongo exception remains `__cause__`.
Authentication/authorization, configuration, capability, index, mapping, filter, and
programmer failures propagate. Driver deadlines map to `timeout`; documented network,
stepdown, shutdown, and retry-labeled failures map to transient retrieval or
persistence categories. Cancellation is observed as `cancelled` and always re-raised.

Direct Memory, History, RAG, index, Session Store, and Checkpoint APIs fail to their
callers. Only Memory and RAG Agent Framework adapter hooks suppress transient
operational failures (and configured timeouts). Memory persistence honors
`persistence_fail_fast`. No adapter suppresses authorization, authentication,
configuration, capability, index, filter, mapping, programmer, or cancellation errors.

## Security boundaries

RAG execution remains read-only in ANN, ENN, full-text, and hybrid modes. Pipelines are
structured mappings built by the provider. Typed filters are translated into
`$vectorSearch.filter`, `$search.compound.filter`, and both `$rankFusion` inputs before
candidate/result limits. Model-facing schemas expose query text but no field, index,
filter, BSON, operator, or pipeline controls. Memory and persistence deletion methods
always add constructor-bound authorization scope and reject empty/unbounded deletion.

Use separate MongoDB principals:

- **RAG runtime:** read and aggregate only on approved knowledge collections; no
  insert, update, replace, upsert, delete, index-management, or cross-database lookup
  privileges.
- **Memory/History runtime:** read/write only their own collections and indexes; do
  not grant Search index administration unless the application explicitly provisions.
- **Session/Checkpoint runtime:** read/write only their respective persistence
  collections.
- **Provisioner:** Search and regular index inspection/management only for approved
  databases and collections. Do not reuse this principal in runtime applications.
- **Test cleanup:** delete/drop only uniquely test-prefixed resources.

Production deployments must use TLS-capable connection strings supplied through the
documented environment variable, with network access restricted to application
egress. Never put a connection string in source, command history, logs, or exception
reporting.

## CI security controls

Credential-free pull requests run the Python 3.10 quality workflow without repository
or deployment secrets. It executes tests and coverage, Ruff, MyPy, Pyright, package
build and Twine validation, and imports from the exact wheel and source distribution in
fresh environments. Separate workflows run the local high-confidence
credential-pattern scanner on every change and GitHub-native CodeQL for Python and
dependency review. Actions receive only their minimum declared token permissions, and
checkout does not persist credentials.

Repository administrators must enable GitHub secret scanning and push protection for
the repository and its supported secret patterns. This platform control is a release
prerequisite because a workflow cannot prove its own secret was blocked before the
workflow started. The local scanner is defense in depth for common private-key, GitHub,
AWS, and credential-bearing MongoDB URI patterns; it is not a replacement for GitHub
secret scanning. No third-party scanner or code upload is configured.

## Troubleshooting

- **No telemetry:** configure a standard Python logging handler and, for spans, an
  application-owned OpenTelemetry tracer provider/exporter. The package exports
  nothing by itself.
- **`authorization` failure:** verify the runtime principal has only the operation's
  required collection privileges. Do not solve runtime failures by granting the
  provisioner role.
- **`index_missing`, `index_mismatch`, or `index_not_ready`:** run the explicit
  inspection/validation API with provisioner credentials, then explicitly create,
  update, or wait. Runtime search never provisions.
- **`capability` or `configuration`:** verify the selected search mode, server and
  driver support, typed filter paths, and index definition. There is no silent
  downgrade.
- **`retrieval`, `persistence`, or `timeout`:** inspect redacted infrastructure
  telemetry outside this package and driver monitoring configured under the
  application's privacy policy. Do not enable raw driver exception logging.
- **Cancellation:** treat it as caller intent. The operation is not converted to an
  empty result and should not be retried automatically.

## Verification

The focused security suite covers the telemetry allowlist and redaction, stable error
categories and causes, cancellation, RAG no-write behavior in every mode, constrained
dependencies, and sample secret patterns. Existing feature suites cover typed filter
placement, fail-open boundaries, unbounded-delete rejection, structured pipelines,
model-facing tool schemas, and index cancellation. Release validation also runs the
full unit/contract suite, Ruff, MyPy, Pyright, package build, Twine metadata checks,
and installation/import checks against the exact wheel and source distribution.
