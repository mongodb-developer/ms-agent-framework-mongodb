# Testing Requirements

## Testing requirements

### Shared test categories

- constructor and option validation
- dependency/client ownership and disposal
- cancellation propagation
- embedding result count and dimension validation
- empty and multimessage query construction
- context injection and source tagging
- error categorization and logging behavior
- no recursive storage of provider-generated context

### Chat History unit tests

- lossless round trips for all supported message content types and additional properties
- deterministic ordering when timestamps collide
- idempotent batch retries and duplicate message handling
- scoped latest-`N` query and chronological return order
- input/context/output filters and provider source attribution
- tool-call and tool-result pairing/order
- clear-session authorization and isolation from Memory
- optional retention/TTL index definition
- incompatible schema/version handling
- concurrent sequence allocation or documented single-writer constraint

### Memory unit tests

- scope validation and storage/search scope differences
- cross-session search by default
- optional session filtering
- batched embedding and insertion
- role filtering
- approximate and exact query pipelines
- index creation, readiness polling, and definition validation
- filter paths and result limits
- deletion by ID/session/user with mandatory scope
- empty/unbounded delete rejection
- deterministic IDs and idempotent retries
- TTL index and optional refresh-on-access writes
- bounded administrative pagination

### RAG unit tests

- vector ANN and ENN stage placement and option exclusivity
- vector, full-text, and hybrid pipeline generation
- score extraction for each mode
- rank-fusion input legality, weights, candidates, final limit, and post-fusion enrichment
- static and session-derived filters
- complete filter translation and rejection of partial translation
- vector prefilters inside `$vectorSearch.filter`
- Search filters inside `$search.compound.filter`
- hybrid mandatory filters in both input pipelines
- filter placement before candidate/result limiting
- nested field mapping
- missing text/title/URL behavior
- normalized result and raw-document preservation
- citation conversion/formatting
- automatic and on-demand invocation modes
- on-demand tool schema exposes query text but no BSON/filter/pipeline control
- composed `.NET TextSearchProvider` cancellation/result compatibility spike
- parent hydration authorization, de-duplication, best-child score, fan-out, and token bounds
- typed metadata query-plan validation and fail-closed translation
- post-retrieval strategy score/source preservation
- optional enrichment stages
- forbidden enrichment and malformed field paths
- capability failures by mode, server, deployment, driver, and index readiness
- read-only behavior: no insert, update, replace, upsert, or delete calls

### Index-manager unit tests

- create command acceptance does not report ready
- missing, building, ready, ready-but-not-queryable, failed, and timeout states
- definition equivalence despite key order/server defaults
- mismatched type, path, dimensions, similarity, filter fields, and Search fields
- update and drop require explicit calls
- monotonic bounded polling
- cancellation during list/create/update/drop and polling delay
- no implicit provisioning from provider hooks or direct search

### Ownership and lifecycle unit tests

- caller-owned clients/databases/collections are never disposed
- provider-created clients are disposed exactly once
- constructor failure and operation failure do not change ownership
- Python async context-manager and .NET `IAsyncDisposable` behavior
- cancellation during embedding, retrieval cursor iteration, persistence, and cleanup
- RAG `after_run`/post-invocation path performs no write
- Memory excludes its own provider-attributed context from subsequent query/storage input

### Persistence unit tests

- Session Store framework serialization round trip and unknown state preservation
- scoped optimistic concurrency create/update/delete conflicts
- Session Store TTL and incompatible schema/framework versions
- checkpoint idempotency, conflict rejection, lineage, sequence ordering, pagination, and latest lookup
- checkpoint resumption with pending approvals and executor state
- expiration-induced lineage gaps are handled and documented

### Integration tests

Run against a real MongoDB deployment with required Search capabilities:

- create isolated test collections with unique prefixes
- create required indexes explicitly
- wait for indexes to become queryable with a bounded timeout
- insert deterministic fixture data
- verify relevant ordering and mandatory tenant filtering
- exercise Memory storage and retrieval
- exercise exact Chat History persistence, reload, continuation, ordering, and clearing
- exercise each supported RAG search mode
- verify ANN and ENN behavior separately
- verify hybrid de-duplication and weight-sensitive ordering with non-tied fixtures
- verify citations/source mapping
- verify parent-document hydration and structured metadata retrieval sample paths
- verify authorization filters prevent cross-tenant candidates in every mode
- verify inspected indexes report `READY` and `queryable` before assertions
- clean up indexes and collections in `finally`/teardown paths
- exercise Session Store serialization, optimistic concurrency, isolation, deletion, and expiration
- exercise Workflow Checkpoint Store save, load, list, latest, resumption, lineage, isolation, and cleanup

Tests requiring external credentials must skip cleanly when credentials are absent. Unit tests must never require
MongoDB or network access.

### Cross-language contract fixtures

The repository MUST maintain language-neutral fixture descriptions for:

- effective Memory scope filters
- ANN/ENN/full-text/hybrid option validation
- logical filter translation outcomes
- normalized RAG results
- source/citation fields
- index-state transitions
- ownership decisions
- exact-history serialization/order/idempotency outcomes
- Session Store serialization and concurrency outcomes
- workflow checkpoint serialization, order, lineage, and resumption outcomes

Fixtures SHOULD be JSON when values are language-neutral, but pipeline tests MAY assert language-specific structured
BSON renderings. Contract tests compare observable semantics and security placement, not incidental serializer casing.

### Capability integration matrix

Every matrix cell advertised as supported MUST have real-deployment evidence in at least one scheduled or release-gate
job. If public CI cannot provision every deployment type, maintainers MUST document the private/manual evidence, date,
versions, and owner. Untested cells MUST be labeled unsupported.
