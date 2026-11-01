#!/usr/bin/env bash
# Shared source-anchor verification for evidence reproduce.sh scripts.
#
# A verified-code commit's evidence stays valid for any later checkout whose
# SOURCE is provably identical: every tracked path outside evidence/ must
# match the verified commit exactly — additions, modifications, deletions,
# renames and file-mode changes all count as drift — and the working tree
# must be clean outside evidence/ so unverified edits cannot smuggle
# themselves into a run. The comparison is symmetric: a path deleted from
# HEAD but present in the verified commit is drift, and so is the reverse.
#
# Usage: verify-source-anchor.sh <verified-commit-sha> [expected-tree-sha]
# Run from anywhere inside the repository. Exit 0 = source provably
# identical (excluding evidence/); exit 2 = absent commit, tree mismatch,
# dirty working tree, source drift, or git failure (reason printed).
set -euo pipefail

VERIFIED="${1:-}"
VERIFIED_TREE="${2:-}"

if [[ -z "$VERIFIED" ]]; then
  echo "verify-source-anchor: usage: $0 <verified-commit-sha> [expected-tree-sha]" >&2
  exit 2
fi

cd "$(git rev-parse --show-toplevel)"

if ! git rev-parse --verify --quiet "${VERIFIED}^{commit}" >/dev/null 2>&1; then
  echo "verify-source-anchor: verified commit ${VERIFIED} is absent from this repository." >&2
  echo "verify-source-anchor: fetch the PR history first (e.g. git fetch origin <pr-branch>)." >&2
  exit 2
fi

if [[ -n "$VERIFIED_TREE" ]]; then
  actual_tree="$(git rev-parse "${VERIFIED}^{tree}")"
  if [[ "$actual_tree" != "$VERIFIED_TREE" ]]; then
    echo "verify-source-anchor: verified commit does not match its recorded tree" >&2
    echo "verify-source-anchor:   recorded=${VERIFIED_TREE}" >&2
    echo "verify-source-anchor:   actual=${actual_tree}" >&2
    exit 2
  fi
fi

# Working tree must not smuggle unverified source edits into the run. The
# pathspec form (not grep) keeps a rename whose destination is under
# evidence/ visible as the source-side deletion it really is.
mapfile -t DIRTY < <(git status --porcelain -- . ':(exclude)evidence')
if (( ${#DIRTY[@]} )); then
  echo "verify-source-anchor: working tree has changes outside evidence/:" >&2
  printf '  %s\n' "${DIRTY[@]}" >&2
  exit 2
fi

# Symmetric source equivalence: git diff between the two commits over every
# path except evidence/. Exit 0 = identical, 1 = differences listed,
# anything else = git error, which must not be read as equivalence.
set +e
DRIFT="$(git diff --name-status "${VERIFIED}" HEAD -- . ':(exclude)evidence')"
diff_rc=$?
set -e
if (( diff_rc > 1 )); then
  echo "verify-source-anchor: git diff failed (exit ${diff_rc}); refusing to verify." >&2
  exit 2
fi
if [[ -n "${DRIFT}" ]]; then
  echo "verify-source-anchor: source differs from verified commit ${VERIFIED}" >&2
  echo "verify-source-anchor: (<status> <path> [<old> -> <new>]):" >&2
  printf '  %s\n' "${DRIFT}" >&2
  echo "verify-source-anchor: refusing to label output as that revision." >&2
  exit 2
fi

echo "verify-source-anchor: HEAD source is identical to ${VERIFIED} outside evidence/."
