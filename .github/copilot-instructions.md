# GitHub Copilot Instructions

This repository provides independently released MongoDB integrations for Microsoft Agent Framework in Python and .NET. Treat [docs/spec/README.md](../docs/spec/README.md) and its linked documents as the canonical implementation specifications, [docs/spec/implementation-map.md](../docs/spec/implementation-map.md) as the required branch and commit order, and `docs/decisions/` as the record of approved architectural choices. Canonical identities are repository `mongo/ms-agent-framework-mongodb`, Python distribution `agent-framework-mongodb` with import root `agent_framework_mongodb`, and .NET package and namespace `MongoDB.AgentFramework`.

## Requirement Language

- `MUST`, `MUST NOT`, and `REQUIRED` requirements are release blockers.
- `SHOULD` and `SHOULD NOT` requirements require an ADR to override.
- Do not silently weaken a requirement. Surface conflicts and update the relevant proposed ADR before implementation.

## Product Boundaries

- Keep Memory, Chat History, RAG, Session Store, and Workflow Checkpoint Store as separate public modules and provider types.
- Memory is semantic conversation recall. Chat History is exact ordered replay. RAG is read-only knowledge retrieval. Session Store persists complete sessions. Workflow Checkpoint Store persists resumable workflow state and lineage.
- Feature modules may depend on shared internal MongoDB mechanics. Shared internals must not depend on feature modules, and feature modules must not call each other.
- Production RAG ingestion, arbitrary MongoDB agent tools, model-generated BSON/pipelines, fact extraction, and graph behavior are out of scope.

## Framework Integration

- Depend only on current public Microsoft Agent Framework contracts. Do not change framework core types for MongoDB-specific behavior.
- Preserve framework source attribution, message filtering, session state, cancellation, and serialization conventions.
- Use `ContextProvider` and `HistoryProvider` in Python and the corresponding public context/history abstractions in .NET.
- Do not subclass sealed .NET types. Compose `TextSearchProvider` only when compatibility tests prove cancellation, citations, score/metadata preservation, and on-demand behavior; otherwise implement a dedicated adapter.

## MongoDB Safety

- Build pipelines with driver builders or structured BSON, never string concatenation.
- Public filters must be typed and operator-limited. Translate the complete mandatory filter into every active retrieval branch or reject it.
- Apply tenant and authorization filters inside `$vectorSearch` and `$search` before candidate or result limiting. Application-side filtering is not an authorization boundary.
- Validate configured field paths, index names, limits, dimensions, and mode-specific options before contacting MongoDB.
- Never expose BSON, field names, operators, filters, index names, or pipelines as model-controlled tool arguments.
- Do not silently downgrade search modes or emulate unsupported MongoDB Search capabilities in application memory.
- Do not create or update indexes during provider construction, agent hooks, or direct search. Provisioning must be an explicit operation.
- Runtime RAG paths are read-only. Add tests that prove no insert, update, replace, upsert, or delete operation occurs.

## Data And Lifecycle

- Make resource ownership immutable at construction. Dispose only provider-created resources; never dispose injected clients, databases, collections, or embedding generators.
- Use stable scoped identifiers and idempotent writes for Memory and Chat History.
- Require an authorization scope for reads and deletion. Never treat a document ID alone as an authorization boundary, and reject unbounded empty deletion filters.
- Preserve exact framework-supported Chat History content in a versioned payload. Do not flatten messages to text.
- Treat stored schemas and index definitions as compatibility surfaces. Reject unknown versions with migration guidance.
- Do not claim Python/.NET physical collection interoperability until cross-language fixtures prove it.

## Errors, Privacy, And Observability

- Validate configuration, capabilities, indexes, filter translation, and mappings with stable integration-level error categories while preserving the driver exception as the cause.
- Direct search, storage, validation, and provisioning APIs fail to the caller. Only agent adapter boundaries may fail open for documented operational errors.
- Always propagate cancellation. Never catch cancellation as an ordinary operational failure.
- Rely on official driver retries first. Any provider retry must be transient-only, bounded by an overall deadline, and safe under idempotency rules.
- Use standard Python logging and `Microsoft.Extensions.Logging`. Do not log credentials, connection strings, embeddings, raw queries, message content, retrieved chunks, or user-bearing filters by default.

## Cross-Language Implementation

- Maintain equivalent observable behavior in Python and .NET while using language-idiomatic APIs.
- Cover shared defaults and behavior with language-neutral fixtures where possible.
- Record and document intentional language differences; do not force identical syntax, BSON casing, serializers, or package versions.
- Use PyMongo's asynchronous API for new Python code. Do not add Motor.
- Use typed MongoDB.Driver builders in .NET where supported and structured BSON only for unsupported stages or options.

## Engineering Workflow

- Before making changes, identify the specific feature, language, search mode, specification sections, and issue or task being implemented.
- Inspect `git branch --show-current`, `git status --short`, and the branch's existing scope before editing.
- Never implement directly on `main`. If the branch is `main`, detached, or belongs to another feature, stop before editing and recommend a branch using `<type>/<scope>-<short-description>` from the appropriate base.
- State the current branch, detected feature scope, reason for mismatch, recommended branch name, and intended base. Do not create or switch branches without explicit user approval.
- Treat unrelated uncommitted changes as user work. Do not move, stash, reset, commit, or carry them to another branch without explicit approval.
- Implement the smallest vertical slice that proves behavior through a public interface.
- Add or update tests with each behavior change. Unit tests must not require network access; credentialed integration tests must skip cleanly when credentials are absent.
- Keep external-test resources uniquely prefixed and ensure cleanup can target only test resources.
- Run the narrowest relevant lint, type, build, and test checks first, then the language quality gate for the affected package.
- Build and smoke test publishable wheel, sdist, and NuGet artifacts rather than relying only on project references.
- Do not mix prototype extraction, public renaming, new RAG behavior, and upstream cleanup in one change.

## Commit Discipline

Follow [CONTRIBUTING.md](../CONTRIBUTING.md) for specification validation, branch workflow, commit sequencing, commit units, message format, pre-commit checks, and history safety.

- Create a commit only when the user explicitly requests it.
- Confirm the branch still matches the feature before staging or committing. Never mix changes from another branch scope into the commit.
- Before coding, map the change to the canonical specifications and implementation-map row, then review its linked ADRs. The specifications authorize the mapped implementation; proposed ADRs record rationale but do not authorize deviations from the specifications.
- Plan the commit series before broad implementation work. Each commit must be a logically separate changeset that leaves the branch buildable, testable, reviewable, and safe to revert independently.
- A commit contains one coherent feature slice, fix, refactor, or infrastructure change. If one subject cannot accurately describe the staged diff, split it.
- Never combine separate product features, RAG modes, language implementations, mechanical refactors, dependency updates, or unrelated cleanup in one commit.
- Keep commits independently buildable, testable, reviewable, and bisectable. Include focused tests and directly associated documentation for the same behavior.
- Order commits by dependency: accepted specification/ADR, shared contract, one language implementation, equivalent language implementation, samples/integration coverage, then packaging and release automation.
- Use `<type>(<scope>): <imperative summary>` with an allowed type and narrow project scope. Keep the subject at 72 characters or fewer.
- Every non-trivial implementation, fix, refactor, performance, security, public API, schema, index, compatibility, or release commit requires a detailed body after a blank line. Explain why the change is needed, the relevant prior behavior, the chosen implementation and important trade-offs, and the validation performed. Do not merely restate the subject or list changed files.
- Use commit footers for issue references, acknowledgments, and `BREAKING CHANGE:` migration details. Do not hide breaking behavior only in the body.
- Do not create placeholder, checkpoint, `WIP`, `fixup!`, or vague follow-up commits in the final series. Fold corrections into the owning commit only through an explicitly approved history-cleanup operation.
- Review the staged diff and run `git diff --cached --check`, the narrowest behavior validation, and the affected quality gate before committing.
- Never commit secrets, local configuration, debug code, unrelated user changes, or knowingly failing tests.
- Do not create, switch, rename, or delete branches, or amend, rebase, squash, force-push, or otherwise rewrite history without explicit user approval.

## Developer Documentation

Developer documentation is a required part of implementation, not a release follow-up. Maintain detailed code-level documentation under `docs/development/`, organized by feature and language, and link it from a local index. Update it in the same commit as the behavior it describes.

- Treat specifications as normative requirements, ADRs as decision rationale, and developer documentation as the maintained explanation of the implemented system. Developer documentation must supplement rather than copy the specifications or ADRs, link to both, and identify the implementation-map slice it realizes.
- Document architecture and design at the level needed to safely modify the code: module responsibilities, ownership boundaries, dependencies, public framework integration points, control and data flow, and why the implementation uses its chosen abstractions.
- Document implementation details that are not obvious from public APIs: algorithms, state transitions, invariants, concurrency and idempotency behavior, serialization and mapping rules, validation order, error translation, cancellation, retries and deadlines, and resource ownership and disposal.
- Document public and extension surfaces with exact symbols and paths: constructors, options, defaults, return types, exceptions, configuration and environment variables, capability gates, privileges, and concise runnable examples.
- For stored or queried data, document BSON schemas, field semantics, identifiers and scopes, versioning, indexes, filter placement, authorization boundaries, migrations, and compatibility implications. Include representative structured documents or pipelines when they clarify behavior, but never include secrets or production data.
- Document observability and operations: emitted logs or traces, required redaction, setup and provisioning, expected failure modes, troubleshooting steps, performance-sensitive choices, known limitations, and externally validated prerequisites.
- Document the verification strategy: focused unit and contract tests, integration fixtures, security assertions, package or sample checks, and the commands that were actually validated. Never claim a command, compatibility range, or deployment behavior that was not verified.
- Record intentional Python/.NET differences and the equivalent observable behavior that preserves parity. Do not force identical internal structure where language conventions differ.
- Prefer precise links to source files, symbols, tests, and existing documents over duplicated prose. Use diagrams or tables when they communicate lifecycle, state, schema, or dependency relationships more clearly than paragraphs.
- Keep documentation current and factual. Remove or revise stale content in the owning code change; do not document speculative behavior as implemented. If code, developer documentation, a specification, and an ADR conflict, stop and resolve the authoritative specification or decision before proceeding.
- Docstrings, XML documentation, comments, examples, and API references complement developer documentation but do not replace the feature-level design and implementation explanation.

## Naming

- Use `MongoDB Search` and `MongoDB Vector Search` unless a requirement is specifically Atlas-only.
- Use the canonical public names and package identities in the requirements until an accepted ADR changes them.
- Never commit credentials or connection strings. Samples must use documented environment variables and provide clear setup failures.

## Architectural Decision Records (ADRs)

ADRs in `docs/decisions/` capture hard-to-reverse decisions and their rationale. Architectural changes, requirement overrides, public schema changes, and package/release policy changes require an ADR.

- New ADRs start as `proposed` and identify deciders, consulted partners, and informed parties.
- Use `adr-template.md` when alternatives and trade-offs matter.
- Use `adr-short-template.md` only for a narrow decision with no material alternative analysis.
- Never treat a proposed ADR as approved. Approval is represented by PR approval and an `accepted` status update.

See [docs/decisions/README.md](../docs/decisions/README.md) for the process and current decision index.
