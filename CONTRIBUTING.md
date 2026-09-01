# Contributing

Contributions must follow the canonical implementation specifications in [docs/spec/README.md](docs/spec/README.md),
the required branch and commit order in [docs/spec/implementation-map.md](docs/spec/implementation-map.md), accepted
decisions in [docs/decisions/](docs/decisions/README.md), and the commit policy below.

## Specification Validation

Before implementation:

1. Identify the smallest observable behavior being changed and the applicable requirement sections.
2. Confirm every applicable `MUST`, `MUST NOT`, and `REQUIRED` statement is satisfied by the proposed change.
3. Confirm the change does not cross the documented Memory, Chat History, RAG, Session Store, or Workflow Checkpoint Store boundaries.
4. Check accepted ADRs for constraints. A proposed ADR is not approval to implement a conflicting design.
5. Resolve an ambiguous or missing requirement before coding. A `SHOULD` deviation requires an accepted ADR.
6. Identify the validation evidence required for the behavior, including unit, contract, integration, compatibility, security, and package tests as applicable.

Public API, stored schema, index definition, package identity, compatibility, security boundary, or release-policy changes require an ADR before implementation. Update the specification and ADR in a dedicated documentation commit before dependent code commits.

## Branch Workflow

Use a short-lived branch for each coherent feature, fix, documentation change, or infrastructure task. Never implement directly on `main`.

Before editing, committing, or generating files:

1. Identify the active feature, language, RAG mode when applicable, and linked specification or issue.
2. Inspect the current branch with `git branch --show-current` and the worktree with `git status --short`.
3. Confirm the branch name and existing commits describe the same feature and scope as the requested work.
4. If the current branch is `main`, detached, or scoped to another task, stop before editing and recommend a correctly named branch based on the latest local `main`.
5. Do not create, switch, reset, rebase, or delete a branch without explicit approval. Do not move unrelated uncommitted changes onto a new branch without confirming ownership and intent.

Branch names use lowercase kebab case:

```text
<type>/<scope>-<short-description>
```

Allowed branch types are `feature`, `fix`, `refactor`, `docs`, `test`, `build`, `ci`, `security`, `deps`, and `release`. Use the same narrow scopes as commit messages, adding a language and feature when relevant.

Examples:

```text
feature/python-memory-scoped-retrieval
feature/dotnet-rag-vector-search
fix/dotnet-history-tool-result-order
docs/adr-chat-history-sequencing
ci/python-package-smoke-tests
security/rag-filter-validation
```

Branch scope rules:

- One branch owns one feature or maintenance objective. Do not use a branch as a container for unrelated backlog work.
- Keep Memory, Chat History, RAG, Session Store, and Workflow Checkpoint Store on separate branches.
- Keep vector, full-text, and hybrid RAG on separate branches unless the branch implements a shared prerequisite with no mode-specific behavior.
- Prefer language-specific feature branches. A cross-language branch is appropriate only for shared contracts, parity fixtures, repository-wide infrastructure, or a deliberately coordinated feature whose commits still keep Python and .NET implementations separate.
- Keep dependency-only, mechanical refactor, formatting, and release work out of feature branches unless strictly required by that feature.
- Base a dependent branch on its prerequisite feature branch only when the dependency is explicit. Otherwise branch from the latest local `main`.
- Keep branches short-lived, focused, and mergeable. Synchronize with `main` using the repository's approved merge or rebase policy; never rewrite a shared branch without approval.

When suggesting a branch, state the detected current branch, the feature/spec scope, the mismatch, the recommended branch name, and the intended base. Example: "Current branch is `main`; this work implements Python Memory scoped retrieval. Create `feature/python-memory-scoped-retrieval` from the latest local `main` before implementation."

## Commit Units

Each commit must represent one coherent feature slice, fix, refactor, or infrastructure change and must be independently reviewable and buildable.

- Include implementation, focused tests, and directly associated documentation for one behavior in the same commit when that keeps the commit green and self-contained.
- Do not combine multiple product features. Memory, Chat History, RAG, Session Store, and Workflow Checkpoint Store changes belong in separate commits.
- Do not combine different RAG modes unless the change is a shared prerequisite with no mode-specific behavior.
- Keep Python and .NET implementation commits separate. Shared contract fixtures may be a preceding cross-language commit.
- Keep mechanical refactors, renames, formatting, dependency updates, generated files, and behavior changes separate from each other.
- Keep bug fixes separate from unrelated cleanup. A bug-fix commit should include a regression test that fails without the fix.
- Keep dependency updates isolated unless a dependency is introduced solely for the one feature in that commit. Include the corresponding lockfile changes.
- Never include secrets, credentials, local settings, unrelated generated artifacts, or another contributor's uncommitted work.
- Never commit a knowingly failing build or test. Temporary red/green TDD steps may remain local, but the committed result must be green.

When a staged diff cannot be described accurately by one short commit subject, split it.

## Commit Sequence

Order commits by dependency so every commit leaves the branch usable:

1. Accepted specification or ADR changes.
2. Shared contracts, fixtures, or internal prerequisites.
3. One language and one feature implementation with focused tests.
4. The equivalent implementation for the other language in a separate commit.
5. Samples and integration coverage for that feature.
6. Packaging, compatibility, CI, or release automation after the behavior it validates exists.

Follow the [implementation map](docs/spec/implementation-map.md) and
[delivery sequence](docs/spec/compatibility-migration.md#delivery-sequence). In particular, do not combine prototype
extraction, public renaming, new RAG behavior, and upstream cleanup. Full-text and hybrid RRF follow vector RAG;
Session Store and Workflow Checkpoint Store follow their shared public serialization contracts in separate
language-specific commits.

## Commit Messages

Use Conventional Commit syntax:

```text
<type>(<scope>): <imperative summary>

<optional body explaining why, constraints, and validation>

<optional issue or breaking-change footer>
```

Allowed types:

- `feat`: new user-visible behavior
- `fix`: defect correction
- `refactor`: behavior-preserving code restructuring
- `perf`: measured performance improvement
- `test`: test-only change
- `docs`: documentation or ADR-only change
- `build`: package or build-system change
- `ci`: workflow or automation change
- `security`: security hardening or vulnerability fix
- `chore`: repository maintenance that fits no type above
- `revert`: explicit reversal of an earlier commit

Use a narrow scope such as `python-memory`, `dotnet-memory`, `python-history`, `dotnet-history`, `python-rag`, `dotnet-rag`, `indexing`, `contracts`, `packaging`, `ci`, `docs`, `security`, or `deps`.

Message rules:

- Use an imperative, lowercase summary with no trailing period.
- Keep the subject at 72 characters or fewer.
- Describe the behavior or outcome, not the files changed or the act of coding.
- Use the body when the reason, trade-off, security effect, migration, or validation is not obvious.
- Reference issues with `Refs: #123` or close them with an appropriate GitHub closing keyword.
- Use a `BREAKING CHANGE:` footer and document migration guidance for incompatible public API, schema, index, or behavior changes.
- Avoid vague subjects such as `updates`, `fix tests`, `changes`, `WIP`, or `misc cleanup`.

Examples:

```text
feat(python-memory): add scoped semantic message retrieval
fix(dotnet-history): preserve tool result order on retry
test(contracts): add cross-language ANN option fixtures
docs(adr): choose atomic chat history sequencing
ci(packaging): smoke test built Python distributions
```

## Pre-Commit Validation

Before creating a commit:

1. Stage explicit paths rather than staging the entire worktree blindly.
2. Review `git status --short` and `git diff --cached --stat` for scope.
3. Review `git diff --cached` for correctness, secrets, debug code, generated noise, and unrelated edits.
4. Run `git diff --cached --check`.
5. Run the narrowest behavior test, then the affected language quality gate.
6. Run contract, integration, package, compatibility, or security checks required by the specification when applicable.
7. Confirm documentation, samples, migration notes, and compatibility matrices match the behavior.
8. Confirm the commit message accurately describes the entire staged diff.

If a required external integration test cannot run, record the reason and remaining evidence in the pull request. Do not claim unsupported validation in the commit message or pull request.

## History Safety

- Create commits only when explicitly requested by the repository owner or active contributor.
- Create, switch, rename, or delete branches only with explicit approval.
- Do not amend, squash, rebase, force-push, or rewrite shared history without explicit approval.
- Do not revert or discard unrelated local changes.
- Preserve authored history when extracting the existing prototypes where practical.
- Before opening a pull request, remove fixup/WIP commits by an approved history-cleanup method and ensure the final sequence remains bisectable.
