#!/usr/bin/env bash
# Reproduce Member A W11 fault-proof evidence (see README.md in this directory).
#
# Provenance model: identical to W09/W10 — this script may run from any
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
# The process-level lanes launch the REAL compiled Trip service as child
# processes (apphost under src/Services/Trip/RideHailing.TripService/bin/),
# terminate them with SIGKILL and restart them; Docker only hosts the
# disposable PostgreSQL/RabbitMQ containers, which are never stopped here.
#
# Writes fresh logs under outputs/repro-<UTC timestamp>/ (gitignored; raw
# runs are never committed — curate results into test-output.txt instead).
set -euo pipefail

VERIFIED_CODE_COMMIT="${VERIFIED_CODE_COMMIT:-6047a7adefb66c4672f0788b8903b37fef48ca44}"
VERIFIED_CODE_TREE="d7a70793df29a7cf4fd430c7c21faa072fb79a47"

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
OUT="evidence/member-a/w11-fault-proofs/outputs/repro-$STAMP"
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
  --filter "FullyQualifiedName~TripW11FaultPointTests" \
  > "$OUT/w11-faultpoint-tests.txt" 2>&1

# A-W11-07: the critical process-level scenarios, five consecutive
# controlled passes. Each pass uses fresh probe databases, fresh identities
# and a fresh faultpoint signal directory; a green pass can only happen if
# the run actually reached its fault window (the tests assert the reached
# marker, the authoritative pre-crash state and the verified process exit).
for pass in 1 2 3 4 5; do
  date -u +"pass${pass}_started=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"
  "$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
    --filter "FullyQualifiedName~TripW11FaultProcessTests" \
    > "$OUT/w11-process-tests-pass${pass}.txt" 2>&1
  date -u +"pass${pass}_finished=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"
done

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW09" \
  > "$OUT/w09-outbox-regression.txt" 2>&1

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW10Saga" \
  > "$OUT/w10-saga-regression.txt" 2>&1

"$DOTNET" test "$TRIP_TESTS" -c Release --no-build --nologo \
  --filter "FullyQualifiedName~TripW08MessagingTests" \
  > "$OUT/w08-messaging-regression.txt" 2>&1

grep -hE "Passed!|Failed!|Skipped!" "$OUT"/*.txt | tee "$OUT/summary.txt"
date -u +"finished=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"

echo "Wrote $OUT"
