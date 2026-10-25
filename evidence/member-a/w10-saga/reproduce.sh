#!/usr/bin/env bash
# Reproduce Member A W10 local evidence (see README.md in this directory).
#
# Provenance model: identical to W09 — this script may run from any checkout
# whose SOURCE is identical to the verified code revision below. The evidence
# history only adds evidence/ files on top of that revision, so instead of
# demanding HEAD == verified commit, every tracked top-level source path (all
# paths except evidence/) must have the SAME git tree hash at HEAD as at the
# verified code commit, and the working tree must be clean outside evidence/.
# Source provenance verification is never skipped or downgraded: any
# difference aborts before a lane runs.
#
# Requires: git, Docker (Testcontainers starts postgres:18.6 and
# rabbitmq:4.3.6-management), .NET SDK 10 (dotnet on PATH, or
# $HOME/.dotnet-sdk/dotnet, or override with DOTNET=...).
#
# Writes fresh logs under outputs/repro-<UTC timestamp>/ (gitignored).
set -euo pipefail

VERIFIED_CODE_COMMIT="${VERIFIED_CODE_COMMIT:-dbbe6253d13e50c1bffa69afe771db2e3d9d2a94}"
VERIFIED_CODE_TREE="975e890a82f4965bfd094f6cf5f83fe895d2001f"

DOTNET="${DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET" && -x "$HOME/.dotnet-sdk/dotnet" ]]; then DOTNET="$HOME/.dotnet-sdk/dotnet"; fi
if [[ -z "$DOTNET" ]]; then
  echo "reproduce: no dotnet SDK found. Install .NET 10, or set DOTNET=/path/to/dotnet." >&2
  exit 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

HEAD_SHA="$(git rev-parse HEAD)"
if ! git rev-parse --verify --quiet "${VERIFIED_CODE_COMMIT}^{commit}" >/dev/null 2>&1; then
  echo "reproduce: verified code commit ${VERIFIED_CODE_COMMIT} is absent from this repository." >&2
  echo "reproduce: fetch the PR history first: git fetch origin feature/A-W10-trip-saga" >&2
  exit 2
fi
if [[ "$(git rev-parse "${VERIFIED_CODE_COMMIT}^{tree}")" != "$VERIFIED_CODE_TREE" ]]; then
  echo "reproduce: verified code commit does not match its recorded tree." >&2
  exit 2
fi

# Working tree must not smuggle unverified source edits into the run.
mapfile -t DIRTY < <(git status --porcelain | grep -v 'evidence/' || true)
if (( ${#DIRTY[@]} )); then
  echo "reproduce: working tree has changes outside evidence/:" >&2
  printf '  %s\n' "${DIRTY[@]}" >&2
  exit 2
fi

# Semantic source equivalence: identical tree hash per top-level path except
# evidence/, at HEAD and at the verified code commit.
source_drift=()
while IFS=$'\t' read -r kind name _; do
  [[ "$name" == "evidence" ]] && continue
  at_head="$(git rev-parse -q --verify "HEAD:${name}" || echo '<absent>')"
  at_verified="$(git rev-parse -q --verify "${VERIFIED_CODE_COMMIT}:${name}" || echo '<absent>')"
  if [[ "$at_head" != "$at_verified" ]]; then
    source_drift+=("$name")
  fi
done < <(git ls-tree HEAD)
if (( ${#source_drift[@]} )); then
  echo "reproduce: source differs from verified code commit ${VERIFIED_CODE_COMMIT}:" >&2
  printf '  %s\n' "${source_drift[@]}" >&2
  echo "reproduce: refusing to label output as that revision." >&2
  exit 2
fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="evidence/member-a/w10-saga/outputs/repro-$STAMP"
mkdir -p "$OUT"

{
  echo "head=$HEAD_SHA"
  echo "verified_code_commit=$VERIFIED_CODE_COMMIT"
  echo "verified_code_tree=$VERIFIED_CODE_TREE"
  echo "source_equivalence=verified (all top-level trees except evidence/ identical)"
  echo "dotnet=$("$DOTNET" --version)"
} | tee "$OUT/header.txt"
date -u +"started=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"

"$DOTNET" build RideHailing.Distributed.slnx -c Release --no-incremental \
  > "$OUT/solution-build-release.txt" 2>&1
echo "build_exit=$?" | tee -a "$OUT/header.txt"

"$DOTNET" test RideHailing.Distributed.slnx -c Release --no-build --nologo \
  > "$OUT/solution-tests-release.txt" 2>&1
echo "solution_tests_exit=$?" | tee -a "$OUT/header.txt"

"$DOTNET" test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripW10Saga" \
  > "$OUT/w10-saga-tests.txt" 2>&1

"$DOTNET" test tests/Unit/RideHailing.TripService.UnitTests/RideHailing.TripService.UnitTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~RideSagaTransitionTests|FullyQualifiedName~TripSagaIntentTests" \
  > "$OUT/w10-saga-unit-tests.txt" 2>&1

"$DOTNET" test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripW09" \
  > "$OUT/w09-outbox-regression.txt" 2>&1

"$DOTNET" test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripW08MessagingTests" \
  > "$OUT/w08-messaging-regression.txt" 2>&1

grep -hE "Passed!|Failed!|Skipped!" "$OUT"/*.txt | tee "$OUT/summary.txt"
date -u +"finished=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"

echo "Wrote $OUT"
