#!/usr/bin/env bash
# Repository PR hygiene guard: deterministic changed-path check (AGENTS.md).
#
# Rejects build output, test dumps, local logs, IDE state, credential files,
# agent planning journals and raw/un-curated execution output from the set of
# paths a change introduces. Canonical evidence (manifests, curated
# test-output.txt, final-state assertions), EF migrations and project source
# are never matched by these patterns. Deletions are always allowed: removing
# a previously tracked transient path is cleanup, not a violation. The guard
# reports the exact offending path and reason and never rewrites anything.
#
# Usage:
#   scripts/test/hygiene-check.sh [base-ref-or-sha]   # changed-path check
#   scripts/test/hygiene-check.sh --self-test         # pattern verdict tests
#
# Base resolution order: explicit argument, pull-request base ref (CI),
# push-event before-SHA (CI), origin/main (local default). Exit 0 = clean,
# exit 1 = rejected paths listed, exit 2 = usage/environment error.
set -euo pipefail

# Classify one changed path. Prints the rejection reason, or nothing when the
# path is acceptable. Single source of patterns for both run modes.
classify() {
  case "$1" in
    bin/*|*/bin/*) echo "build output (bin/) is never durable" ;;
    obj/*|*/obj/*) echo "build output (obj/) is never durable" ;;
    TestResults/*|*/TestResults/*) echo "test-result dump (TestResults/)" ;;
    *.trx) echo "test-result dump (.trx)" ;;
    *.log|*.log.*) echo "local log file" ;;
    *.user|*.suo|*.DotSettings.user) echo "IDE personal state" ;;
    .vs/*|*/.vs/*|.idea/*|*/.idea/*) echo "IDE directory" ;;
    plans/*|*/plans/*) echo "agent planning journal (plans/ is local-only)" ;;
    evidence/*/outputs/*) echo "raw execution output (evidence outputs/ is local-only; curate test-output.txt instead)" ;;
    evidence/*/raw/*|evidence/*/raw) echo "raw evidence dump (see .gitignore)" ;;
    *.dump|*.tmp|*.bak) echo "scratch file" ;;
    .env|*/.env) echo "environment secrets" ;;
    *.pfx) echo "credential material" ;;
    secrets/*|*/secrets/*) echo "credential material" ;;
  esac
}

self_test() {
  local -a cases=(
    "reject|logs/solution-build-attempt.log"
    "reject|src/Services/Trip/run.log"
    "reject|evidence/member-a/w09-outbox/outputs/solution-build-release.txt"
    "reject|evidence/member-a/w09-outbox/outputs/repro-20261025T042747Z/header.txt"
    "reject|evidence/member-a/w10-saga/outputs/nested/raw.txt"
    "reject|TestResults/tests.trx"
    "reject|tests/Unit/X/TestResults/run.trx"
    "reject|plans/session-transcript.md"
    "reject|scratch/plans/agent-journal.md"
    "reject|obj/Release/net10.0/app.dll"
    "reject|src/Service/bin/Debug/app.dll"
    "reject|.vs/config/applicationhost.config"
    "reject|secrets/prod.env"
    "reject|deploy/.env"
    "accept|evidence/member-a/w09-outbox/test-output.txt"
    "accept|evidence/member-a/w09-outbox/manifest.json"
    "accept|evidence/member-a/w09-outbox/reproduce.sh"
    "accept|evidence/member-a/w10-saga/final-state/rel-09-compensation.md"
    "accept|evidence/member-a/w10-saga/metrics/pending-age.md"
    "accept|src/Services/Trip/RideHailing.TripService/Migrations/20261025031423_W09_TripOutbox.cs"
    "accept|tests/Integration/RideHailing.TripService.IntegrationTests/TripW10SagaTests.cs"
    "accept|docs/03-verification-experiments-demo.md"
    "accept|AGENTS.md"
    "accept|scripts/test/hygiene-check.sh"
  )
  local fail=0 verdict reason path case_entry
  for case_entry in "${cases[@]}"; do
    verdict="${case_entry%%|*}"
    path="${case_entry#*|}"
    reason="$(classify "$path")"
    if [[ "$verdict" == "reject" && -z "$reason" ]]; then
      echo "hygiene-check self-test: FAIL expected reject, got accept: ${path}" >&2
      fail=1
    elif [[ "$verdict" == "accept" && -n "$reason" ]]; then
      echo "hygiene-check self-test: FAIL expected accept, got reject (${reason}): ${path}" >&2
      fail=1
    fi
  done
  if (( fail )); then
    echo "hygiene-check self-test: FAILED" >&2
    exit 1
  fi
  echo "hygiene-check self-test: ${#cases[@]} pattern verdicts OK."
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

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

# Deletions (D) are deliberately excluded from the filter: this guard judges
# what a change INTRODUCES, and removing a tracked transient path must never
# be a violation. Paths are read NUL-separated so odd characters survive.
mapfile -d '' -t CHANGED < <(git diff --name-only -z --diff-filter=ACMRT "${BASE}...HEAD")

fail=0
for path in "${CHANGED[@]}"; do
  reason="$(classify "$path")"
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
