#!/usr/bin/env bash
# Reproduce Member A W12 core-gate evidence (see README.md in this directory).
#
# Provenance model: identical to W09/W10/W11 — this script may run from any
# checkout whose SOURCE is provably identical to the verified code revision
# below. Source equivalence (every tracked path outside evidence/ — additions,
# modifications, deletions, renames and mode changes all count as drift, and
# the working tree must be clean outside evidence/) is verified symmetrically
# by the shared scripts/test/verify-source-anchor.sh and is never skipped:
# any difference aborts before a lane runs.
#
# Requires: git, Docker (Testcontainers starts postgres:18.6 and
# rabbitmq:4.3.6-management), .NET SDK 10 (dotnet on PATH, or
# $HOME/.dotnet-sdk/dotnet, or override with DOTNET=...).
#
# Writes fresh logs under outputs/repro-<UTC timestamp>/ (gitignored; raw
# runs are never committed — curate results into test-output.txt instead).
set -euo pipefail

VERIFIED_CODE_COMMIT="${VERIFIED_CODE_COMMIT:-976c7f8662e05c0abe17a0dedcf8a45d94b03592}"
VERIFIED_CODE_TREE="${VERIFIED_CODE_TREE:-c8ef3dbd9a2a3bf8d5de989a4f8cfedc531e5187}"

DOTNET="${DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET" && -x "$HOME/.dotnet-sdk/dotnet" ]]; then DOTNET="$HOME/.dotnet-sdk/dotnet"; fi
if [[ -z "$DOTNET" ]]; then
  echo "reproduce: no dotnet SDK found. Install .NET 10, or set DOTNET=/path/to/dotnet." >&2
  exit 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

HEAD_SHA="$(git rev-parse HEAD)"

# Shared, symmetric source-anchor verification (exits 2 on any drift).
bash scripts/test/verify-source-anchor.sh "$VERIFIED_CODE_COMMIT" "$VERIFIED_CODE_TREE"

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="evidence/member-a/w12-core-gate/outputs/repro-$STAMP"
mkdir -p "$OUT"

{
  echo "head=$HEAD_SHA"
  echo "verified_code_commit=$VERIFIED_CODE_COMMIT"
  echo "verified_code_tree=$VERIFIED_CODE_TREE"
  echo "source_equivalence=verified (every tracked path outside evidence/ identical: no add/modify/delete/rename/mode drift)"
  echo "dotnet=$("$DOTNET" --version)"
} | tee "$OUT/header.txt"
date -u +"started=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"

TRIP_TESTS="tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj"

"$DOTNET" build RideHailing.Distributed.slnx -c Release --no-incremental \
  > "$OUT/solution-build-release.txt" 2>&1
echo "build_exit=$?" | tee -a "$OUT/header.txt"

"$DOTNET" test RideHailing.Distributed.slnx -c Release --no-build --nologo \
  > "$OUT/solution-tests-release.txt" 2>&1
echo "solution_tests_exit=$?" | tee -a "$OUT/header.txt"

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW12TelemetryTests" \
  > "$OUT/w12-telemetry.txt" 2>&1

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW12TracePropagationTests" \
  > "$OUT/w12-trace-propagation.txt" 2>&1

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW12AuthzNegativeTests" \
  > "$OUT/w12-authz-negatives.txt" 2>&1

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW12InvariantAuditorTests" \
  > "$OUT/w12-invariant-auditor.txt" 2>&1

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW09" \
  > "$OUT/w09-outbox-regression.txt" 2>&1

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW10Saga" \
  > "$OUT/w10-saga-regression.txt" 2>&1

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW11Fault" \
  > "$OUT/w11-fault-regression.txt" 2>&1

docker compose -f compose.yaml config --quiet > "$OUT/compose-config.txt" 2>&1
echo "compose_config_exit=$?" | tee -a "$OUT/header.txt"

bash scripts/test/hygiene-check.sh > "$OUT/hygiene-guard.txt" 2>&1
echo "hygiene_guard_exit=$?" | tee -a "$OUT/header.txt"

grep -hE "Passed!|Failed!|Skipped!" "$OUT"/*.txt | tee "$OUT/summary.txt"
date -u +"finished=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"

echo "Wrote $OUT"
