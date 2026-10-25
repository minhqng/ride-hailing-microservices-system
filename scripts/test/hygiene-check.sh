#!/usr/bin/env bash
# Repository PR hygiene guard: deterministic changed-path check (AGENTS.md).
#
# Rejects build output, test dumps, local logs, IDE state, credential files,
# agent planning journals and raw/un-curated reproduction runs from the set of
# paths a change introduces. Canonical evidence (manifests, curated
# test-output, final-state assertions), EF migrations and project source are
# never matched by these patterns. The guard reports the exact offending path
# and reason and never rewrites anything.
#
# Usage:
#   scripts/test/hygiene-check.sh [base-ref-or-sha]
#
# Base resolution order: explicit argument, pull-request base ref (CI),
# push-event before-SHA (CI), origin/main (local default). Exit 0 = clean,
# exit 1 = rejected paths listed, exit 2 = usage/environment error.
set -euo pipefail

BASE="${1:-}"
if [[ -z "$BASE" && -n "${GITHUB_BASE_REF:-}" ]]; then BASE="origin/${GITHUB_BASE_REF}"; fi
if [[ -z "$BASE" && -n "${GITHUB_EVENT_BEFORE:-}" &&
      "${GITHUB_EVENT_BEFORE}" != "0000000000000000000000000000000000000000" ]]; then
  BASE="${GITHUB_EVENT_BEFORE}"
fi
BASE="${BASE:-origin/main}"

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

if ! git rev-parse --verify --quiet "${BASE}^{commit}" >/dev/null 2>&1; then
  echo "hygiene-check: base '${BASE}' is not resolvable; nothing to compare." >&2
  exit 0
fi

mapfile -t CHANGED < <(git diff --name-only --diff-filter=ACMRDT "${BASE}...HEAD")

fail=0
for path in "${CHANGED[@]}"; do
  reason=""
  case "$path" in
    bin/*|*/bin/*) reason="build output (bin/) is never durable" ;;
    obj/*|*/obj/*) reason="build output (obj/) is never durable" ;;
    TestResults/*|*/TestResults/*) reason="test-result dump (TestResults/)" ;;
    *.trx) reason="test-result dump (.trx)" ;;
    *.log|*.log.*) reason="local log file" ;;
    *.user|*.suo|*.DotSettings.user) reason="IDE personal state" ;;
    .vs/*|*/.vs/*|.idea/*|*/.idea/*) reason="IDE directory" ;;
    plans/*|*/plans/*) reason="agent planning journal (plans/ is local-only)" ;;
    evidence/*/outputs/repro-*|evidence/*/outputs/repro-*/*) \
      reason="regenerated reproduction run (curate a summary; keep raw runs local)" ;;
    evidence/*/raw/*|evidence/*/raw) reason="raw evidence dump (see .gitignore)" ;;
    *.dump|*.tmp|*.bak) reason="scratch file" ;;
    .env|*/.env) reason="environment secrets" ;;
    *.pfx) reason="credential material" ;;
    secrets/*|*/secrets/*) reason="credential material" ;;
  esac
  if [[ -n "$reason" ]]; then
    echo "hygiene-check: REJECT ${path} — ${reason}" >&2
    fail=1
  fi
done

if (( fail )); then
  echo "hygiene-check: remove the rejected paths, or curate their durable content per AGENTS.md / docs/03 §25." >&2
  exit 1
fi

echo "hygiene-check: ${#CHANGED[@]} changed path(s) clean."
