#!/usr/bin/env bash
# Fixture tests for scripts/test/verify-source-anchor.sh.
#
# Builds throwaway git repositories and asserts the anchor verifier accepts a
# checkout that differs from the verified commit only under evidence/, and
# rejects every other shape of source drift (added, modified, deleted —
# including whole top-level paths — renamed, mode-changed, renamed into
# evidence/) plus dirty working trees, unverified source smuggled as
# renames, absent commits and tree mismatches. Runs in CI (hygiene job).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERIFIER="${SCRIPT_DIR}/verify-source-anchor.sh"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

pass=0
fail=0

expect() { # expect <ok|drift> <label>
  local want="$1" label="$2" out rc=0
  out="$(bash "$VERIFIER" "$VERIFIED" "$VERIFIED_TREE" 2>&1)" || rc=$?
  if [[ "$want" == "ok" && "$rc" -eq 0 ]]; then
    pass=$((pass + 1))
  elif [[ "$want" == "drift" && "$rc" -eq 2 ]]; then
    pass=$((pass + 1))
  else
    echo "verify-source-anchor.test: FAIL ${label}: exit=${rc} want=${want}" >&2
    printf '  %s\n' "$out" >&2
    fail=1
  fi
}

new_fixture() {
  rm -rf "$WORK/fixture"
  git init -q "$WORK/fixture"
  cd "$WORK/fixture"
  git config user.email fixture@test && git config user.name fixture
  mkdir -p src/deep scripts evidence/bundle
  echo a > src/a.cs
  echo deep > src/deep/d.cs
  echo b > scripts/b.sh
  chmod +x scripts/b.sh
  echo top > TOPLEVEL.md
  echo e > evidence/bundle/manifest.json
  git add -A
  git commit -qm base
  VERIFIED="$(git rev-parse HEAD)"
  VERIFIED_TREE="$(git rev-parse HEAD^{tree})"
  # The checkout under test always carries legitimate evidence-only changes.
  mkdir -p evidence/bundle
  echo curated > evidence/bundle/test-output.txt
  git add evidence
  git commit -qm evidence-only
}

# 1. Evidence-only delta: accepted.
new_fixture
expect ok "evidence-only delta"

# 2. Whole top-level path deleted relative to the verified commit.
new_fixture
git rm -rq scripts && git commit -qm del-top-level
expect drift "top-level path deleted"

# 3. Nested file deleted.
new_fixture
git rm -q src/deep/d.cs && git commit -qm del-nested
expect drift "nested file deleted"

# 4. Nested file modified.
new_fixture
echo changed > src/deep/d.cs && git add -A && git commit -qm modify
expect drift "nested file modified"

# 5. New tracked source path.
new_fixture
mkdir plans && echo p > plans/p.md && git add plans && git commit -qm add
expect drift "source path added"

# 6. Top-level file renamed.
new_fixture
git mv TOPLEVEL.md README_TOP.md && git commit -qm rename
expect drift "top-level rename"

# 7. File-mode change on a top-level file.
new_fixture
chmod +x TOPLEVEL.md && git add TOPLEVEL.md && git commit -qm mode
expect drift "file-mode change"

# 8. Source file renamed into evidence/ (committed): the source side is drift.
new_fixture
git mv src/a.cs evidence/bundle/moved.md && git commit -qm mv-into-evidence
expect drift "source renamed into evidence/"

# 9. Dirty working tree: untracked source file.
new_fixture
echo rogue > src/rogue.cs
expect drift "untracked source file"

# 10. Dirty working tree: source deleted, untracked copy parked under evidence/.
new_fixture
git rm -q src/a.cs
cp src/deep/d.cs evidence/bundle/d.cs
expect drift "working-tree rename into evidence/"

# 11. Verified commit absent from the repository.
new_fixture
out="$(bash "$VERIFIER" 0000000000000000000000000000000000000000 2>&1)" || rc=$?
if [[ "$rc" -eq 2 ]]; then pass=$((pass + 1)); else
  echo "verify-source-anchor.test: FAIL absent commit: exit=${rc}" >&2; fail=1
fi

# 12. Recorded tree hash does not match the verified commit.
new_fixture
out="$(bash "$VERIFIER" "$VERIFIED" 0000000000000000000000000000000000000000 2>&1)" || rc=$?
if [[ "$rc" -eq 2 ]]; then pass=$((pass + 1)); else
  echo "verify-source-anchor.test: FAIL tree mismatch: exit=${rc}" >&2; fail=1
fi

if (( fail )); then
  echo "verify-source-anchor.test: FAILED" >&2
  exit 1
fi
echo "verify-source-anchor.test: ${pass} fixture cases OK."
