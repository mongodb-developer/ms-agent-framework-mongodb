# Python Atlas Agent Engine managed memory

This is the additive [implementation-map slice](../../spec/implementation-map.md#additive-python-preview-slice)
for [Atlas Agent Engine managed memory](../../spec/features/atlas-agent-engine-memory.md).
[ADR 0019](../../decisions/0019-separate-atlas-agent-engine-managed-memory.md) explains why this
is separate from direct MongoDB Memory. There is intentionally no .NET equivalent for this preview.

## Public surface and flow

`agent_framework_mongodb.atlas_agent_engine_memory.AtlasAgentEngineMemoryContextProvider`
implements the public `ContextProvider` lifecycle. It takes the official SDK `Memory` client
configured for a hosted project, required `user_id`, and optional `source_id`
(`atlas-agent-engine-memory`), `record_turns` (`False`), `include_short_term` (`False`),
`max_tokens` (`2048`, greater than `500`), and `persistence_fail_fast` (`False`). It does
not own or close the client or its bound handles. Dependencies are loaded at provider
construction, not when importing the base distribution. The preview extra pins the SDK to
`0.11.15` for Python 3.11+. The [quickstart](../../../python/README.md#atlas-agent-engine-managed-memory)
shows Agent Framework registration and credential variables.

- `before_run` builds a query from new plain-text user inputs and requests private semantic and
  episodic context scoped to the trusted user. If enabled, STM also receives the current session
  scope. Only nonempty selected chunks enter the prompt via framework source attribution.
- `after_run` opts in per provider, uses only current user and assistant text, and ignores
  the replay and retrieval context buckets, attributed messages, and tool content. It validates
  every eligible message's key before writing. Keys hash user, session, role, and either the
  message ID or the response ID plus eligible-message position. To deduplicate a logically
  retried Agent run across *different* model response IDs, supply stable message IDs. The SDK
  sends the same caller key on retry. Acknowledgement is not confirmation of extraction.
- The SDK's synchronous network calls run off the event loop. There are no additional adapter
  retries. Cancellation propagates, but cannot undo a request already sent to the service.
  The caller must keep the SDK client open until any in-flight call finishes.

## Lifecycle and failure handling

The adapter owns no collection schema, index, extraction logic, or retention policy. The hosted
service may extract durable data from recorded turns. Configure and apply the Memory runtime
outside the application, manage deletion at the service, and send only opted-in data. When
composing both backends, the distinct framework sources prevent recursive recording; retaining
both writers intentionally stores data twice. Direct, app-bound, and deployed runner modes are
not part of this adapter.

Authorization, capability, configuration, and unacknowledged writes surface explicit
integration errors. Transient SDK connection/server failures fail open at the adapter hooks;
`persistence_fail_fast=True` also surfaces transient recording failures. Existing structured
logs and traces use the `atlas_agent_engine_memory` feature and `retrieve` or `persist` operation.
They do not include SDK error text, queries, replies, user IDs, or credentials.

## Verification

The focused unit tests exercise public framework hooks, private scoping, source attribution,
write opt-in, replay exclusion, stable keys, errors, and cancellation without network access.
Run the SDK contract test with the extra installed:
`python -m pytest -q tests/contracts/test_atlas_agent_engine_memory_contract.py`. It invokes a
real Agent Framework agent and the official SDK over mock HTTP, without Atlas credentials.

Run `python -m pytest -m integration_atlas_agent_engine` only against a disposable project.
Configure its Memory runtime for STM with durable extraction disabled so the same user's
cross-session recall cannot return extracted versions of the fixture. Set
`ATLAS_AGENT_ENGINE_TEST_PROJECT_ID`, `AGENTIC_MEMORY_SERVICE_ACCOUNT_TOKEN`, and
`ATLAS_AGENT_ENGINE_TEST_DISPOSABLE=1` in the local process. The test stores uniquely prefixed
synthetic turns; dispose of the dedicated project after testing. It never issues deletion calls.
No hosted result is implied when skipped.
