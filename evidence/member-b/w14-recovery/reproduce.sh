#!/usr/bin/env bash
set -euo pipefail
ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"
bash scripts/test/verify-source-anchor.sh \
  212c98fbcf4f3c1056e9f0dd9017af8b2a7e3a2f \
  fb5189ab857ec6c415215333d5fb2d7e620e1a14
if command -v python3 >/dev/null 2>&1; then PYTHON=python3; else PYTHON=python; fi
if ! command -v dotnet >/dev/null 2>&1; then export PATH="$HOME/.dotnet:$PATH"; fi
export TESTCONTAINERS_RYUK_DISABLED=true
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
export MSBUILDDISABLENODEREUSE=1
export B12_AUDIT_OUTPUT_DIR="$ROOT/evidence/member-b/w14-recovery/outputs/reproduce-auditor"
export B12_AUDIT_SOURCE_COMMIT="$(git rev-parse HEAD)"
export B12_AUDIT_SOURCE_TREE="$(git rev-parse 'HEAD^{tree}')"
"$PYTHON" scripts/perf/w14.py analyze evidence/member-b/w14-recovery/functional-smoke
"$PYTHON" scripts/perf/w13.py validate
dotnet build RideHailing.Distributed.slnx -c Release -p:UseSharedCompilation=false
for project in \
  tests/Unit/RideHailing.DriverService.UnitTests \
  tests/Unit/RideHailing.MatchingService.UnitTests \
  tests/Integration/RideHailing.DriverService.IntegrationTests \
  tests/Integration/RideHailing.MatchingService.IntegrationTests \
  tests/Audit/RideHailing.B12Audit.Tests; do
  dotnet test "$project" -c Release --no-build
done
bash scripts/test/hygiene-check.sh 16dc175a3b205115f6c71aeee00b41c22cad420b
git diff --check
# A new disposable E04 execution is explicit; recorded smoke is never a report campaign.
if [[ "${1:-}" == "--new-smoke" ]]; then
  "$PYTHON" scripts/perf/w14.py e04 --mode smoke --rate 0.5 --outage 8 --baseline 10
fi
