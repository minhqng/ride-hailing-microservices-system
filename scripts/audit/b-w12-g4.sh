#!/usr/bin/env bash
# B-W12-08: executable test-scope invariant auditor for Driver/Matching G4.
#
# Runs the B-owned authoritative auditor over disposable PostgreSQL
# environments, writes the machine-readable result bundle, and fails when any
# expected positive/negative assertion fails. Corruption controls are isolated
# in per-scenario databases; audit reads use read-only transactions.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  else
    echo "error: dotnet not found (expected on PATH or $HOME/.dotnet)" >&2
    exit 2
  fi
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "error: docker not found; the auditor requires disposable PostgreSQL containers" >&2
  exit 2
fi

OUT_DIR="${B12_AUDIT_OUTPUT_DIR:-$ROOT/evidence/member-b/w12-observability-g4/auditor}"
mkdir -p "$OUT_DIR"
export B12_AUDIT_OUTPUT_DIR="$OUT_DIR"
export B12_AUDIT_SOURCE_COMMIT="${B12_AUDIT_SOURCE_COMMIT:-$(git rev-parse HEAD)}"
export B12_AUDIT_SOURCE_TREE="${B12_AUDIT_SOURCE_TREE:-$(git rev-parse 'HEAD^{tree}')}"
export B12_AUDIT_SOURCE_STATUS="${B12_AUDIT_SOURCE_STATUS:-$(git status --porcelain | tr '\n' ';' | cut -c1-2000)}"
export TESTCONTAINERS_RYUK_DISABLED=true

PROJECT="tests/Audit/RideHailing.B12Audit.Tests/RideHailing.B12Audit.Tests.csproj"

echo "=== B-W12-08 invariant auditor ==="
echo "source commit : $B12_AUDIT_SOURCE_COMMIT"
echo "source tree   : $B12_AUDIT_SOURCE_TREE"
echo "evidence dir  : $OUT_DIR"
echo "project       : $PROJECT"
echo

dotnet test "$PROJECT" -c Release \
  --logger "console;verbosity=normal" \
  --filter "FullyQualifiedName~AuditScenarioTests" \
  "$@"

for artifact in auditor-results.json auditor-results.md negative-controls.md; do
  if [[ ! -s "$OUT_DIR/$artifact" ]]; then
    echo "error: missing auditor artifact $OUT_DIR/$artifact" >&2
    exit 3
  fi
done

echo
echo "=== Auditor result summary ==="
grep -E '"PASS"|"LOCAL_PROXY_PASS"|"FAIL"|"NOT_RUN"|"BLOCKED"' "$OUT_DIR/auditor-results.json" | head -n 5 || true
echo "Artifacts:"
echo "  $OUT_DIR/auditor-results.json"
echo "  $OUT_DIR/auditor-results.md"
echo "  $OUT_DIR/negative-controls.md"
