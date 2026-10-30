#!/usr/bin/env bash
# Reproduce the W13 harness smoke and analysis without overwriting the
# curated evidence bundle. Outputs land under gitignored outputs/repro-*/.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT"
unset W13_E02_TOKEN_FILE W13_G4_EVIDENCE W13_G4_GREEN W13_RUNS_ROOT || true
HARNESS="$ROOT/scripts/perf/w13.sh"

VERIFIED_COMMIT="b1beb8451f50673bc98445beea7f3c6b6d8f7c4a"
VERIFIED_TREE="4033938a500e6373e59f3d346a0bc0481a5e2744"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$ROOT/evidence/member-b/w13-performance-campaign/outputs/repro-$STAMP"
mkdir -p "$OUT/runs"

echo "=== source anchor ==="
bash scripts/test/verify-source-anchor.sh "$VERIFIED_COMMIT" "$VERIFIED_TREE"

echo "=== evidence hashes ==="
( cd "$ROOT/evidence/member-b/w13-performance-campaign" && sha256sum -c hashes.txt )

echo "=== harness validation ==="
"$HARNESS" validate

echo "=== E01 smoke ==="
W13_RUNS_ROOT="$OUT/runs" "$HARNESS" e01 --mode smoke --n 1,2,10

echo "=== E02 readiness boundary (expected BLOCKED while G4 is red) ==="
E02_CODE=0
W13_RUNS_ROOT="$OUT/runs" "$HARNESS" e02 --mode smoke || E02_CODE=$?
if [[ "$E02_CODE" != "3" ]]; then
  echo "reproduce: E02 unexpectedly returned $E02_CODE (expected 3 / BLOCKED)" >&2
  exit 2
fi

echo "=== E03 smoke ==="
W13_RUNS_ROOT="$OUT/runs" "$HARNESS" e03 --mode smoke --replicas 1,2 --rate 5 --duration 5s

echo "=== analysis ==="
ANALYZE_CODE=0
"$HARNESS" analyze --runs-root "$OUT/runs" --out-dir "$OUT/analysis" || ANALYZE_CODE=$?
if [[ "$ANALYZE_CODE" != "3" ]]; then
  echo "reproduce: analysis unexpectedly returned $ANALYZE_CODE (expected 3 / NOT_ACCEPTED)" >&2
  exit 2
fi

cat <<EOF

Reproduction completed. Regenerated outputs:
  $OUT

Expected classifications: E01 SMOKE_PASS, E02 BLOCKED, E03 SMOKE_PASS,
campaign NOT_ACCEPTED. These are harness smoke results, not report-grade
performance acceptance. The authoritative B-owned auditor is reproduced with:

  "$HARNESS" audit --out "$OUT/auditor"

and still requires a clean tree outside evidence/.
EOF
