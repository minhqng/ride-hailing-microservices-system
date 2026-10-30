#!/usr/bin/env bash
# Reproduce Member A W09 local evidence (see README.md in this directory).
#
# Provenance model: this script may run from any checkout whose SOURCE is
# provably identical to the verified code revision below. Source equivalence
# (every tracked path outside evidence/ — additions, modifications,
# deletions, renames and mode changes all count as drift, and the working
# tree must be clean outside evidence/) is verified symmetrically by the
# shared scripts/test/verify-source-anchor.sh and is never skipped: any
# difference aborts before a lane runs.
#
# Requires: git, Docker (Testcontainers starts postgres:18.6 and
# rabbitmq:4.3.6-management), .NET SDK 10 (dotnet on PATH, or
# $HOME/.dotnet-sdk/dotnet, or override with DOTNET=...).
#
# Writes fresh logs under outputs/repro-<UTC timestamp>/ (gitignored; raw
# runs are never committed — curate results into test-output.txt instead).
set -euo pipefail

VERIFIED_CODE_COMMIT="${VERIFIED_CODE_COMMIT:-0185c3755bd2887c9bd73287604608911ca10ed2}"
VERIFIED_CODE_TREE="c430e1786b214c029f13283db0dadb3282a967df"

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
OUT="evidence/member-a/w09-outbox/outputs/repro-$STAMP"
mkdir -p "$OUT"

{
  echo "head=$HEAD_SHA"
  echo "verified_code_commit=$VERIFIED_CODE_COMMIT"
  echo "verified_code_tree=$VERIFIED_CODE_TREE"
  echo "source_equivalence=verified (every tracked path outside evidence/ identical: no add/modify/delete/rename/mode drift)"
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
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripW09" \
  > "$OUT/w09-outbox-tests.txt" 2>&1

"$DOTNET" test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripW08MessagingTests" \
  > "$OUT/w08-messaging-tests.txt" 2>&1

"$DOTNET" test tests/Unit/RideHailing.TripService.UnitTests/RideHailing.TripService.UnitTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripOutboxIntentTests" \
  > "$OUT/w09-intent-unit-tests.txt" 2>&1

grep -hE "Passed!|Failed!|Skipped!" "$OUT"/*.txt | tee "$OUT/summary.txt"
date -u +"finished=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"

echo "Wrote $OUT"
