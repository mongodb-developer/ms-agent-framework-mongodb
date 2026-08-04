# .NET Agent Framework compatibility matrix

This document supplements [dotnet-packaging-release.md](dotnet-packaging-release.md)
and is governed by the same specifications:
[packages.md](../../spec/packages.md),
[quality-release.md](../../spec/quality-release.md), and
[compatibility-migration.md](../../spec/compatibility-migration.md). It
documents implementation-map [slice 20](../../spec/implementation-map.md)'s
explicit Microsoft Agent Framework compatibility matrix and the mechanism
used to verify it without ever changing the package's tracked default
dependency range.

## Matrix bounds

`MongoDB.AgentFramework.csproj` depends on `Microsoft.Agents.AI.Abstractions`
and `Microsoft.Agents.AI.Workflows` via the range `[1.13.0, 1.17.0)`. The two
bounds below are the ones this repository has independently
reflection-verified against MongoDB.AgentFramework's actual usage (see
[Session Store contract verification](../persistence/dotnet-contract-research.md)
and [Checkpoint Store contract verification](../persistence/dotnet-checkpoint-contract-research.md)):

| Agent Framework version | Role                                | Verified                                             |
| ------------------------ | ------------------------------------ | ----------------------------------------------------- |
| `1.13.0`                  | Minimum supported (range floor)      | Yes -- CI matrix + local script                       |
| `1.16.0`                  | Newest verified (latest published minor below the `1.17.0` exclusive ceiling as of this writing) | Yes -- CI matrix + local script |

Every version strictly between `1.13.0` and `1.16.0` is covered by the same
NuGet range and is expected, but not independently reflection-verified, to
behave identically; the ceiling `1.17.0` is deliberately *excluded* from the
range until it is itself reflection-verified (see the range's own inline
comment in `MongoDB.AgentFramework.csproj`).

## Why an MSBuild property, not a repository copy

The compatibility matrix must prove MongoDB.AgentFramework actually restores,
builds, and passes its unit test suite against each exact matrix bound, not
merely that the *range* is wide enough to contain them (that fact is already
implied by the range declaration itself). Two mechanisms were considered:

1. **Copy/checkout the repository into a scratch directory per matrix
   version and edit the copy's `.csproj`.** Rejected: heavier, and still
   requires *some* mechanism to substitute the version, so it does not avoid
   the core problem -- it just moves the edit to a throwaway copy instead of
   avoiding the edit.
2. **A single `$(AgentFrameworkVersion)` MSBuild property, referenced by both
   `PackageReference` elements, defaulting to the tracked range when not
   overridden.** Chosen: no tracked file is ever edited for a matrix run;
   `-p:AgentFrameworkVersion=1.13.0` (or `1.16.0`) on the command line is the
   *only* difference between a default build and a matrix-bound build, and a
   plain `dotnet build`/`dotnet pack`/`dotnet test` with no override keeps
   resolving the exact same tracked range as before this matrix existed.

Both `dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj` and
`dotnet/tests/MongoDB.AgentFramework.Tests/MongoDB.AgentFramework.Tests.csproj`
declare:

```xml
<PropertyGroup>
  <AgentFrameworkVersion Condition="'$(AgentFrameworkVersion)' == ''">[1.13.0,1.17.0)</AgentFrameworkVersion>
</PropertyGroup>
```

and reference `Microsoft.Agents.AI.Abstractions`/`Microsoft.Agents.AI.Workflows`
with `Version="$(AgentFrameworkVersion)"` instead of a literal range. The test
project needs its own copy of this property (rather than only inheriting
`MongoDB.AgentFramework`'s resolved version through the `ProjectReference`)
because it also carries an *explicit* `Microsoft.Agents.AI.Workflows`
`PackageReference` of its own (see that project file's inline comment on why
that reference is explicit, not merely transitive).

## Local verification

`dotnet/scripts/verify-agent-framework-compatibility.ps1` runs the full
matrix locally:

```powershell
pwsh dotnet/scripts/verify-agent-framework-compatibility.ps1 -Configuration Release
```

`-Configuration` defaults to `Release` (matching `verify-package.ps1` and
every `dotnet test`/`dotnet build` invocation in CI), so it can be omitted
locally, but CI passes it explicitly so the exact documented invocation is
what actually runs.

For each of `1.13.0` and `1.16.0` (overridable via `-Versions`), it:

1. Cleans `src/MongoDB.AgentFramework` and
   `tests/MongoDB.AgentFramework.Tests`'s `bin`/`obj`, and any leftover TRX
   results from a previous run (a stale `project.assets.json` or TRX file
   from a previously pinned version would otherwise mask a real
   restore/test failure, or a false "passed" reading).
2. Restores `MongoDB.AgentFramework.Tests.csproj` with
   `-p:AgentFrameworkVersion=<version>` -- NuGet's restore graph follows its
   `<ProjectReference>` to `MongoDB.AgentFramework.csproj` automatically, so
   this single restore covers the exact pinned version for both the package
   under test and its test suite.
3. Builds (Release) `MongoDB.AgentFramework.csproj` (the exact thing that gets
   packed) and both credential-free test projects, each with
   `--no-restore` (restore already happened in step 2).
4. Reads the resulting `project.assets.json` and asserts
   `Microsoft.Agents.AI.Abstractions` and `Microsoft.Agents.AI.Workflows`
   both resolved to *exactly* the requested version (not merely "a version
   satisfying the range") and prints the resolved transitive
   `Microsoft.Extensions.Logging.Abstractions` version so a reviewer can
   confirm it stayed within this package's own declared
   `[10.0.9, 11.0.0)` range at both matrix bounds.
5. Runs `dotnet test` against `MongoDB.AgentFramework.Tests.csproj` and
   `IngestionSamples.Tests.csproj` with
   `--no-build --no-restore` (the build already happened in step 3, so this
   can only execute already-built tests, never silently no-op through an
   implicit rebuild) and unique per-project TRX loggers, then parses each file's
   `<ResultSummary><Counters>` element and asserts its `executed` attribute
   is strictly greater than zero. This is deliberate: `dotnet test
   --no-restore` alone against an unrestored/stale test project can exit `0`
   having executed **zero** tests (MSBuild silently no-ops the VSTest target
   when it cannot evaluate the test SDK's `IsTestProject` property from a
   missing restore) -- a console-output or exit-code check alone would never
   catch this. Skipped credentialed integration tests count toward the TRX's
   `total`, not its `executed` count, so they never mask a genuine
   zero-unit-tests-executed failure.
6. Packs and local-feed consumer-smokes the package with both real Agent
   Framework dependencies pinned to the exact requested version.
7. Cleans `bin`/`obj` while retaining TRX/JSON/Markdown evidence, so a
   subsequent plain `dotnet build`/`dotnet pack` resolves the tracked range
   exactly as before.

Both matrix bounds were verified locally: `Microsoft.Agents.AI.Abstractions`
and `Microsoft.Agents.AI.Workflows` each resolved to the exact requested
version (`1.13.0`/`1.16.0`), `Microsoft.Extensions.Logging.Abstractions`
resolved to `10.0.9` at both bounds (within the declared
`[10.0.9, 11.0.0)` range). The refined `1.16.0` run executed **1,100 tests**
(974 provider tests and 126 ingestion-sample tests) with credentialed
integration tests skipped, retaining one TRX per project -- real TRX-derived
counts, not console-output heuristics.

## CI: `dotnet-agent-framework-compat`

`.github/workflows/dotnet-quality.yml` first resolves the latest and immediately
previous common listed stable versions from the official NuGet V3 registration
APIs, then runs a `dotnet-agent-framework-compat` matrix invoking the same
`verify-agent-framework-compatibility.ps1` script with
`-Configuration Release -Versions "<exact version>"`. It is a separate job
(not an extra `dotnet-quality` matrix dimension) because it re-restores/
re-builds against a genuinely different dependency version per entry, rather
than exercising the `dotnet-quality` job's own OS/tooling variance. It
requires no secrets and runs on every pull request, including from forks. This
is upstream drift evidence and does not silently widen `[1.13.0,1.17.0)`.

The manual/scheduled `dotnet-agent-framework-compatibility.yml` workflow tests
latest stable, latest preview if one exists, and an optional exact common
listed version. Missing preview is explicit, never a stable substitution.
Every row now also packs and local-feed consumer-smokes the package, retaining
TRX plus machine-readable JSON and Markdown reports. See
[release operations](dotnet-release-operations.md).

## Microsoft.Extensions.* transitive compatibility

`MongoDB.AgentFramework.csproj` also directly references
`Microsoft.Extensions.AI.Abstractions` (`[10.7.0, 11.0.0)`) and
`Microsoft.Extensions.Logging.Abstractions` (`[10.0.9, 11.0.0)`). The floor
of `10.0.9` was already raised from `10.0.0` (see the property's inline
comment) specifically because `Microsoft.Agents.AI.Workflows 1.13.0`
transitively requires `Microsoft.Extensions.Logging.Abstractions >= 10.0.9`;
this matrix confirms that constraint continues to hold, unchanged, at the
newest verified bound (`1.16.0`) as well -- both matrix runs resolved the
same `10.0.9`, so `Microsoft.Agents.AI.Workflows` did not raise its own
transitive floor between `1.13.0` and `1.16.0`.
