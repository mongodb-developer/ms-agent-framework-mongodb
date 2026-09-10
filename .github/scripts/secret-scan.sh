#!/usr/bin/env bash
# Local, pattern-based repository secret scan.
#
# This intentionally does NOT download or execute any third-party binary (no gitleaks CLI, no
# other scanner). It is a dependency-free `git grep` gate satisfying the "secret scan available
# locally" requirement without the supply-chain and licensing questions that come with fetching a
# release artifact. It can be run identically in CI (see .github/workflows/dotnet-security.yml)
# and on a developer machine with only `git` and `bash` installed.
#
# Known limitations (documented per policy, not fixed here):
#   - Scans only the files tracked by git at the current checkout (`git grep` over the working
#     tree/index), not full git history. A secret that was committed and later removed will not
#     be caught by this script; use a history-aware scanner for that guarantee if ever required.
#   - Pattern-based only: it recognizes known credential/token shapes (cloud provider keys, private
#     key headers, connection strings with embedded credentials) plus a generic
#     "name-looks-like-a-secret and is assigned a literal value" heuristic. It has no entropy
#     analysis and will miss secrets that do not match one of these shapes.
#   - The generic heuristic explicitly excludes this repository's own `SENTINEL-SECRET-...` test
#     fixtures (see dotnet/tests/MongoDB.AgentFramework.Tests/Observability/), which are
#     intentional, non-sensitive plaintext markers used by tests to prove telemetry redaction, not
#     real secrets.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

status=0
tracked_count=$(git ls-files | wc -l | tr -d ' ')
echo "Scanning ${tracked_count} tracked files for common secret patterns..."

report() {
  local label="$1"
  shift
  echo "=== ${label} ==="
  if git grep -n -I "$@" -- . 2>/dev/null; then
    status=1
  fi
}

report "MongoDB connection strings with embedded credentials" \
  -E 'mongodb(\+srv)?://[^:@/[:space:]]+:[^@/[:space:]]+@'

report "AWS access key IDs" \
  -E 'AKIA[0-9A-Z]{16}'

report "GitHub tokens" \
  -E '(ghp_|gho_|ghu_|ghs_|ghr_|github_pat_)[A-Za-z0-9_]{20,}'

report "Slack tokens" \
  -E 'xox[baprs]-[A-Za-z0-9-]{10,}'

report "Google API keys" \
  -E 'AIza[0-9A-Za-z_-]{35}'

report "Private key material" \
  -E 'BEGIN (RSA |EC |DSA |OPENSSH )?PRIVATE KEY'

echo "=== Hardcoded credential-like assignments ==="
credential_assignment_dq='(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[:=]\s*"[^"[:space:]]{8,}"'
credential_assignment_sq="(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\\s*[:=]\\s*'[^'[:space:]]{8,}'"
credential_hits=""
if matches=$(git grep -n -I -i -P "$credential_assignment_dq" -- . 2>/dev/null); then
  credential_hits="${credential_hits}${matches}"$'\n'
fi
if matches=$(git grep -n -I -i -P "$credential_assignment_sq" -- . 2>/dev/null); then
  credential_hits="${credential_hits}${matches}"$'\n'
fi
credential_hits=$(printf '%s' "$credential_hits" | grep -v -F 'SENTINEL-SECRET-' || true)
if [ -n "$credential_hits" ]; then
  echo "$credential_hits"
  status=1
fi

if [ "$status" -ne 0 ]; then
  echo "Potential secret(s) found by pattern scan. Review the matches above." >&2
  exit 1
fi

echo "No secret patterns found."
