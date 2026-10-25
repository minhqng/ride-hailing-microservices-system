#!/usr/bin/env bash
# Reproduce Member A W09 local evidence at code commit 63f13a5544dd5b1caed357b62c3e60a188113ed4.
# Writes fresh logs under outputs/repro-<UTC timestamp>/.
# Requires: Docker (Testcontainers), .NET SDK 10.0.401.
set -euo pipefail

DOTNET="${DOTNET:-$HOME/.dotnet-sdk/dotnet}"
EXPECTED_COMMIT="${EXPECTED_COMMIT:-63f13a5544dd5b1caed357b62c3e60a188113ed4}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

ACTUAL_COMMIT="$(git rev-parse HEAD)"
if [[ "$ACTUAL_COMMIT" != "$EXPECTED_COMMIT" ]]; then
  echo "Refusing to label output as commit $EXPECTED_COMMIT: HEAD is $ACTUAL_COMMIT." >&2
  echo "Check out the evidence commit or set EXPECTED_COMMIT explicitly." >&2
  exit 2
fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="evidence/member-a/w09-outbox/outputs/repro-$STAMP"
mkdir -p "$OUT"

echo "commit=$ACTUAL_COMMIT tree=$(git rev-parse HEAD^{tree})" | tee "$OUT/header.txt"
date -u +"started=%Y-%m-%dT%H:%M:%SZ" | tee -a "$OUT/header.txt"

"$DOTNET" build RideHailing.Distributed.slnx -c Release --no-incremental \
  > "$OUT/solution-build-release.txt" 2>&1
echo "build_exit=$?" | tee -a "$OUT/header.txt"

"$DOTNET" test RideHailing.Distributed.slnx -c Release --no-build --nologo \
  > "$OUT/solution-tests-release.txt" 2>&1
echo "solution_tests_exit=$?" | tee -a "$OUT/header.txt"

"$DOTNET" test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripW09OutboxTests" \
  > "$OUT/w09-outbox-tests.txt" 2>&1

"$DOTNET" test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripW08MessagingTests" \
  > "$OUT/w08-messaging-tests.txt" 2>&1

"$DOTNET" test tests/Unit/RideHailing.TripService.UnitTests/RideHailing.TripService.UnitTests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName~TripOutboxIntentTests" \
  > "$OUT/w09-intent-unit-tests.txt" 2>&1

grep -hE "Passed!|Failed!|Skipped!" "$OUT"/*.txt | tee "$OUT/summary.txt"

echo "Wrote $OUT"
