# Pull Request

## Problem

<!-- What user or maintainer problem does this solve? Link the issue with a closing keyword when applicable. -->

Fixes #

## Approach

<!-- Describe the observable behavior, implementation boundary, and alternatives considered. -->

## Scope

- Feature area: <!-- Memory / Chat History / RAG / Indexing / Persistence / Shared / Docs -->
- Language: <!-- Python / .NET / Both / Not applicable -->
- Search mode: <!-- ANN / ENN / Full text / Hybrid / Not applicable -->
- Source branch: <!-- <type>/<scope>-<short-description> -->

## Requirements And Decisions

<!-- Link relevant requirements and ADRs. Explain any SHOULD-level deviation in a new or updated ADR. -->

- Requirements:
- ADRs:
- Public API or stored-schema impact:
- Compatibility or migration impact:

## Security, Privacy, And Lifecycle

<!-- Describe tenant/filter placement, reads and writes, ownership/disposal, retention/deletion, cancellation, logging/redaction, and index privilege changes. Write "None" only after checking each area. -->

## Validation

<!-- List exact commands and integration environments. Never include credentials, private content, embeddings, or connection strings. -->

- [ ] Added or updated focused unit tests.
- [ ] Added or updated language-neutral contract fixtures when behavior is shared.
- [ ] Ran the affected Python and/or .NET quality gate.
- [ ] Built and smoke-tested affected package artifacts.
- [ ] Ran real MongoDB integration tests, or documented why they were not applicable/available.
- [ ] Verified cancellation and provider/caller resource ownership where applicable.
- [ ] Verified mandatory filters execute in MongoDB before limiting in every retrieval branch.
- [ ] Verified RAG runtime paths remain read-only where applicable.
- [ ] Updated public documentation, compatibility matrices, samples, and migration notes as needed.

## Commit Quality

- [ ] The source branch is short-lived, follows `<type>/<scope>-<short-description>`, and contains one feature or maintenance objective.
- [ ] The branch was created from the correct base and does not contain unrelated work.
- [ ] Each commit contains one feature slice, fix, refactor, or infrastructure change.
- [ ] Separate product features, RAG modes, and Python/.NET implementations are not combined in one commit.
- [ ] Commits follow the dependency and delivery sequence in the requirements.
- [ ] Every commit is independently buildable, testable, reviewable, and bisectable.
- [ ] Commit messages follow `<type>(<scope>): <imperative summary>` and describe the complete staged change.
- [ ] Specification/ADR changes precede dependent implementation commits.
- [ ] Fixup, WIP, formatting-only noise, and unrelated changes are absent from the final history.

## Review Checklist

- [ ] The change uses only public Agent Framework contracts.
- [ ] The change preserves Memory, Chat History, RAG, Session Store, and Workflow Checkpoint boundaries.
- [ ] Pipelines use driver builders or structured BSON without string interpolation.
- [ ] No secrets, user content, embeddings, raw queries, or retrieved chunks are logged by default.
- [ ] Index provisioning is explicit and never runs in provider hooks or direct search.
- [ ] Python and .NET behavior is equivalent, or the intentional difference is documented.
- [ ] This is not a breaking change. If it is, explain the versioning and migration plan above and apply the `breaking change` label.
