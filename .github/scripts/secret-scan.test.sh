#!/usr/bin/env bash
# Lightweight self-test for secret-scan.sh's own detection logic.
#
# secret-scan.sh's actual job is to scan *this* repository, so the only way to prove it still detects real
# secrets and still respects its documented SENTINEL-SECRET- exclusion (without ever committing a real-looking
# secret into this repository's own history) is to run it against small, disposable scratch git repositories
# created for exactly this test. No third-party tooling is used -- only `bash` and `git`, matching
# secret-scan.sh's own dependency-free design. Run locally exactly as CI does:
#   bash .github/scripts/secret-scan.test.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
scan_script="$script_dir/secret-scan.sh"

scratch=$(mktemp -d)
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT

make_scratch_repo() {
  local dir="$1"
  mkdir -p "$dir"
  git init -q "$dir"
  git -C "$dir" config user.email "test@example.invalid"
  git -C "$dir" config user.name "secret-scan self-test"
}

commit_all() {
  local dir="$1"
  git -C "$dir" add -A
  git -C "$dir" commit -q -m "scratch fixture"
}

# secret-scan.sh always resolves its target repository via `git rev-parse --show-toplevel` from the current
# directory, so running it from inside each scratch repo scans only that scratch repo, never this real one.
run_scan_in() {
  local dir="$1"
  (cd "$dir" && bash "$scan_script")
}

failures=0

assert_fails() {
  local label="$1" dir="$2"
  if run_scan_in "$dir" > /dev/null 2>&1; then
    echo "FAIL: expected secret-scan.sh to detect a secret in '${label}' fixture, but it exited 0."
    failures=$((failures + 1))
  else
    echo "PASS: secret-scan.sh detected the '${label}' fixture as expected."
  fi
}

assert_passes() {
  local label="$1" dir="$2"
  if run_scan_in "$dir" > /dev/null 2>&1; then
    echo "PASS: secret-scan.sh reported no findings for the '${label}' fixture as expected."
  else
    echo "FAIL: expected secret-scan.sh to report no findings for '${label}' fixture, but it exited non-zero."
    failures=$((failures + 1))
  fi
}

# Case 1: a synthetic AWS access key ID must be detected. Built from two halves at runtime (never as one
# contiguous literal in this file) so this self-test script itself never contains a string secret-scan.sh's
# own AWS-key pattern would match -- it must only appear in the disposable scratch fixture it writes below.
positive_dir="$scratch/positive"
make_scratch_repo "$positive_dir"
aws_key_prefix="AKIA"
aws_key_rest="ABCDEFGHIJKLMNOP"
printf 'const string Key = "%s%s";\n' "$aws_key_prefix" "$aws_key_rest" > "$positive_dir/secret.cs"
commit_all "$positive_dir"
assert_fails "AWS access key" "$positive_dir"

# Case 2: the documented SENTINEL-SECRET- test-fixture exclusion must still be honored.
sentinel_dir="$scratch/sentinel"
make_scratch_repo "$sentinel_dir"
printf 'private const string Secret = "SENTINEL-SECRET-0123456789abcdef";\n' > "$sentinel_dir/fixture.cs"
commit_all "$sentinel_dir"
assert_passes "SENTINEL-SECRET- exclusion" "$sentinel_dir"

# Case 3: a clean tree with no secret-shaped content must report no findings.
clean_dir="$scratch/clean"
make_scratch_repo "$clean_dir"
printf 'public sealed class Nothing { }\n' > "$clean_dir/clean.cs"
commit_all "$clean_dir"
assert_passes "clean tree" "$clean_dir"

if [ "$failures" -ne 0 ]; then
  echo "secret-scan.sh self-test failed (${failures} case(s))." >&2
  exit 1
fi

echo "secret-scan.sh self-test passed."
