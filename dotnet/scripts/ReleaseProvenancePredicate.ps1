<#
.SYNOPSIS
    Builds a CUSTOM SLSA v1.0 build-provenance predicate (https://slsa.dev/provenance/v1) that explicitly binds a
    MongoDB.AgentFramework release's attested package artifacts to a validated, ancestry-proven immutable commit
    SHA, for `dotnet-release-attestation.yml`'s `provenance-attestation` job to attest via the generic
    `actions/attest` action's custom-predicate mode.

.DESCRIPTION
    The stock `actions/attest-build-provenance` action (a thin wrapper over `actions/attest`'s default,
    auto-generated provenance mode) populates its SLSA predicate's build-source information solely from the
    running job's own ambient `GITHUB_SHA`/`GITHUB_REF` environment values -- the same values the runner's OIDC
    token derives its claims from. Under `dotnet-release-attestation.yml`'s `workflow_run` trigger (deliberately
    the ONLY trigger this repository ever grants `id-token`/`attestations`/`artifact-metadata` permissions to --
    see that workflow's own header comment), GitHub's own event-reference documentation defines those values as
    "Last commit on default branch"/"Default branch": i.e. THIS WORKFLOW'S OWN trigger context (main's current
    tip), never the validated upstream release commit the `provenance-attestation` job actually checks out and
    rebuilds from. Checking out a different commit into the job's workspace does not alter `GITHUB_SHA`/
    `GITHUB_REF` or the OIDC claims derived from them -- those are fixed for the whole job at start-up by the
    runner, not by any step's own `actions/checkout` `ref:` input. Attesting with the stock auto-provenance mode
    would therefore have silently produced misleading provenance: a signed statement whose "source" commit is
    main's unrelated current tip, not the actual validated tag/commit whose packed bytes are the attestation's
    real subject. This repository's release policy forbids publishing misleading provenance (see
    docs/development/release/dotnet-packaging-release.md's "Known blockers"), so this predicate is instead
    hand-built to explicitly record the REAL validated commit as a `resolvedDependencies` entry, while
    `runDetails.builder` still correctly and honestly identifies this workflow/ref as the actual builder (which
    it genuinely is -- `workflow_run` guarantees this file's own content is always sourced from `main`).

    New-ReleaseProvenancePredicate is a pure, dependency-free function (no I/O) so it can be exercised directly by
    ReleaseProvenancePredicate.tests.ps1 without a real GitHub Actions run. write-release-provenance-predicate.ps1
    is the thin CLI wrapper the real workflow invokes, which serializes this function's result to a JSON file for
    `actions/attest`'s `-predicate-path` input.

    Schema reference (https://slsa.dev/spec/v1.0/provenance#schema): the emitted object is the raw `predicate`
    field's CONTENT only (`buildDefinition`/`runDetails`) -- `actions/attest` itself fills in the enclosing
    in-toto Statement's `_type`/`subject`/`predicateType` fields from its own `-subject-path`/`-predicate-type`
    inputs, so this function must never wrap its output in an extra `predicate`/`predicateType` envelope.
#>

<#
.SYNOPSIS
    Builds the custom SLSA v1.0 provenance predicate content binding a release build to a validated commit SHA.

.PARAMETER ValidatedSha
    The EXACT, already ancestry-proven-against-origin/main commit SHA `dotnet-release-attestation.yml`'s
    `validate-attestation-eligibility` job validated and `provenance-attestation` checked out and rebuilt from.
    Must be a well-formed 40-character lowercase hexadecimal git commit SHA -- this function refuses (throws) any
    other shape, so a malformed/truncated/empty value can never silently reach a published attestation.

.PARAMETER RepositorySlug
    The GitHub `owner/repo` slug (e.g. `github.repository`), used to build fully-qualified `builder.id`/
    `resolvedDependencies[].uri`/`metadata.invocationId` URLs. Must be in `owner/repo` form (exactly one `/`).

.PARAMETER RunId
    `github.run_id` of the attesting workflow run -- recorded in `runDetails.metadata.invocationId` for
    traceability. Not validated beyond non-empty (GitHub always provides a real numeric run id).

.PARAMETER RunAttempt
    `github.run_attempt` of the attesting workflow run -- recorded alongside `RunId` in `invocationId`.

.PARAMETER IsTagPush
    Whether the validated release originated from a trusted `dotnet-v<version>` tag push (as opposed to a
    main-only manual `workflow_dispatch`) -- recorded as an informational `externalParameters.validatedRelease`
    field, matching `dotnet-release-attestation.yml`'s own `validate-attestation-eligibility` job output.

.PARAMETER TagName
    The validated tag name (only meaningful when -IsTagPush is set); empty for a manual main dispatch.

.PARAMETER WorkflowPath
    Path (repo-relative) to the attesting workflow file. Defaults to
    `.github/workflows/dotnet-release-attestation.yml` -- the one workflow file this predicate is ever generated
    from.

.PARAMETER WorkflowRef
    The ref the attesting workflow file itself was sourced from. Defaults to `refs/heads/main`, which is always
    correct for this predicate's caller: `dotnet-release-attestation.yml`'s ONLY trigger is `workflow_run`, which
    GitHub's own documentation guarantees always resolves and runs the reacting workflow's file exactly as it
    exists on the repository's default branch, regardless of what ref/event triggered the upstream run.

.OUTPUTS
    [System.Collections.Specialized.OrderedDictionary] the predicate content
    (`buildDefinition`/`runDetails`), ready for `ConvertTo-Json -Depth 10`.
#>
function New-ReleaseProvenancePredicate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ValidatedSha,
        [Parameter(Mandatory)][string]$RepositorySlug,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$RunAttempt,
        [Parameter(Mandatory)][bool]$IsTagPush,
        [AllowEmptyString()][string]$TagName = "",
        [string]$WorkflowPath = ".github/workflows/dotnet-release-attestation.yml",
        [string]$WorkflowRef = "refs/heads/main"
    )

    # A malformed/truncated/empty SHA must never silently reach a published attestation's resolvedDependencies --
    # this is the one field the whole predicate exists to bind honestly, so it is validated strictly here as a
    # second, independent layer of defense on top of the caller's own ancestry/eligibility checks.
    if ($ValidatedSha -cnotmatch '^[0-9a-f]{40}$') {
        throw "ValidatedSha '$ValidatedSha' is not a well-formed 40-character lowercase hexadecimal git commit SHA -- refusing to bind release provenance to an unverifiable or malformed commit reference."
    }

    if ([string]::IsNullOrWhiteSpace($RepositorySlug) -or $RepositorySlug -cnotmatch '^[^/]+/[^/]+$') {
        throw "RepositorySlug '$RepositorySlug' must be in 'owner/repo' form (e.g. 'mongo/ms-agent-framework-mongodb')."
    }

    if ($IsTagPush -and [string]::IsNullOrWhiteSpace($TagName)) {
        throw "IsTagPush was true but TagName was empty -- a tag-push-derived predicate must record the actual validated tag name."
    }

    $repositoryUrl = "https://github.com/$RepositorySlug"

    $predicate = [ordered]@{
        buildDefinition = [ordered]@{
            buildType            = "$repositoryUrl/blob/main/$WorkflowPath"
            externalParameters   = [ordered]@{
                workflow         = [ordered]@{
                    ref        = $WorkflowRef
                    repository = $repositoryUrl
                    path       = $WorkflowPath
                }
                validatedRelease = [ordered]@{
                    isTagPush = $IsTagPush
                    tagName   = $TagName
                }
            }
            internalParameters   = [ordered]@{
                github = [ordered]@{
                    eventName  = "workflow_run"
                    runId      = $RunId
                    runAttempt = $RunAttempt
                }
            }
            # The one field this whole predicate exists to bind honestly: the REAL commit that was checked out
            # and rebuilt, independent of this job's own ambient GITHUB_SHA/GITHUB_REF (which always describe
            # this workflow's own trigger context under `workflow_run`, never the artifact's actual source
            # commit -- see this file's top-level rationale comment).
            resolvedDependencies = @(
                [ordered]@{
                    name   = "validated-release-commit"
                    uri    = "git+$repositoryUrl@$ValidatedSha"
                    digest = [ordered]@{ gitCommit = $ValidatedSha }
                }
            )
        }
        runDetails      = [ordered]@{
            builder  = [ordered]@{
                # The actual builder honestly IS this workflow file at this ref -- `workflow_run` guarantees its
                # content is always sourced from the default branch, so this identity claim is always accurate,
                # unlike the stock auto-provenance mode's source-commit claim under the same trigger.
                id = "$repositoryUrl/$WorkflowPath@$WorkflowRef"
            }
            metadata = [ordered]@{
                invocationId = "$repositoryUrl/actions/runs/$RunId/attempts/$RunAttempt"
            }
        }
    }

    return $predicate
}
