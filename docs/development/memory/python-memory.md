# Python Memory implementation

This document describes implementation-map
[slice 2](../../spec/implementation-map.md), governed by the
[Memory specification](../../spec/features/memory.md), the
[public interface contract](../../spec/interfaces.md), and ADR rationale
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md), and
[0015](../../decisions/0015-default-memory-persistence-to-fail-open.md).
The ADRs remain proposed and do not weaken the specifications.

## Public boundary and ownership

`agent_framework_mongodb.MongoDBMemoryContextProvider` derives from Agent
Framework `ContextProvider`. It accepts an embedding generator and exactly one
MongoDB ownership path: an injected async collection, an injected async client,
or a URI used to create a provider-owned client. Construction performs no
network, embedding, or index operation. `close()` is idempotent and closes only
the provider-created client.

At least one of `application_id`, `agent_id`, or `user_id` is required. These
immutable constructor scopes are applied inside every `$vectorSearch.filter`
and every lifecycle query. Search crosses sessions by default; passing
`session_id` adds an in-stage session filter. `max_results` is bounded to 100
and `num_candidates` to 10,000. Optional positive `retrieval_timeout` and
`persistence_timeout` values bound their complete embedding/database operation
and raise `MongoDBTimeoutError` at direct API boundaries.

## Storage, retrieval, and framework flow

`store()` selects non-empty text from user, assistant, and system messages,
excluding all provider-attributed context. It calls the embedding generator
once per batch, validates count, dimensions, numeric type, and finiteness, then
uses one unordered `insert_many`. Documents use the lowercase schema from the
Memory specification. A configured positive `retention` adds `expires_at`;
permanent records omit it.

Document IDs are SHA-256 hashes of immutable scope plus framework message ID.
When no message ID exists, one UUID is generated and retained in the
provider-scoped Agent Framework pending-batch state, so a failed `after_run`
retry reuses the same ID. Pending state is removed only after confirmed
insertion or a duplicate-only idempotent replay; a later successful run with
identical content therefore receives a new ID.

`search()` embeds one non-empty query and builds structured BSON for either ANN
(`numCandidates`) or ENN (`exact: true`). The scope filter is inside
`$vectorSearch`, before limiting. It returns Agent Framework `Message` values
with origin session metadata. `before_run()` combines input text, retrieves
cross-session memories, adds the configured untrusted-data prompt, and injects
messages through `SessionContext.extend_messages`, which supplies source
attribution. `after_run()` stores caller input and response while excluding
provider context.

Direct `search()` and `store()` calls always surface stable integration errors
with the PyMongo or generator exception as `__cause__`. Adapter retrieval and
persistence suppress only operational retrieval/persistence categories and
emit content-free warnings. Cancellation and configuration/mapping/index
errors propagate. `persistence_fail_fast=True` makes adapter persistence
operational errors visible to applications requiring transactional durability.

## Lifecycle and administration

- `delete_memory(id)` requires the configured authorization scope in addition
  to `_id`.
- `clear_session(session_id)` combines session and configured scope.
- `clear_user()` requires a configured user and retains application/agent scope.
- `list_metadata()` uses bounded (maximum 100) `_id` keyset pagination and
  projects no content or embeddings.

Deletion is visible on the primary deployment but MongoDB backups, replicas,
and application audit records have independent retention obligations.

## Explicit indexes

No constructor, hook, direct search, or storage path provisions indexes.
`create_vector_search_index()`, `ensure_vector_search_index()`,
`validate_vector_search_index()`, and
`wait_until_vector_search_index_ready()` are explicit Vector Search operations.
Validation checks the name, vector path, dimensions, similarity, all four scope
filter fields, `READY` status, and queryability.

`ensure_regular_indexes()` is intentionally separate. It creates a compound
administrative scope index and, only when retention is configured, a regular
TTL index on `expires_at` with `expireAfterSeconds: 0`. TTL deletion is
eventual. Retrieval is read-only and never refreshes expiration.
`list_regular_indexes()` and `validate_regular_indexes()` provide non-mutating
inspection and definition validation.

Runtime roles need collection read/write privileges; lifecycle calls need
delete privileges. Index provisioning should use a separate principal allowed
to manage Search and regular indexes. Production URIs should use TLS and the
deployment must permit the application's network path.

## Verification

Unit tests under `python/tests/unit/test_memory_*.py` mock only the embedding
and MongoDB boundaries. The language-neutral scope fixture under
`tests/fixtures/memory/` is exercised through the public search API by the
Python contract test.

Credentialed `python/tests/integration_memory/test_memory_integration.py`
creates a uniquely prefixed collection, explicitly provisions and waits for
the index, exercises ENN storage/retrieval/deletion, proves an equally relevant
cross-tenant memory is excluded, and targets only that collection in `finally`.
It skips unless `MONGODB_URI` and
`MONGODB_DATABASE` are set.

The Python gate is pytest, Ruff lint and format checks, mypy, Pyright, wheel and
sdist build, Twine validation, and clean installation/import from each exact
artifact. Real-deployment evidence remains environment-specific and is not
claimed when the credentialed test skips.
