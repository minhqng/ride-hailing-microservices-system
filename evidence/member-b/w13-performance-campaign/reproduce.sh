#!/usr/bin/env bash
# Reproduce the W13 harness smoke and analysis without overwriting the
# curated evidence bundle. Outputs land under gitignored outputs/repro-*/.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT"

VERIFIED_COMMIT="1dcc7ee0f7b7c1575cbe4cbddec6a6211a097a58"
VERIFIED_TREE="4a0dd7b805b5ed15c7f3e067a0b127135e614e89"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$ROOT/evidence/member-b/w13-performance-campaign/outputs/repro-$STAMP"
mkdir -p "$OUT/runs"

echo "=== source anchor ==="
bash scripts/test/verify-source-anchor.sh "$VERIFIED_COMMIT" "$VERIFIED_TREE"

echo "=== harness validation ==="
python scripts/perf/w13.py validate

echo "=== E01 smoke ==="
W13_RUNS_ROOT="$OUT/runs" python scripts/perf/w13.py e01 --mode smoke --n 1,2,10

echo "=== E02 readiness boundary (expected BLOCKED while G4 is red) ==="
E02_CODE=0
W13_RUNS_ROOT="$OUT/runs" python scripts/perf/w13.py e02 --mode smoke || E02_CODE=$?
if [[ "$E02_CODE" != "3" ]]; then
  echo "reproduce: E02 unexpectedly returned $E02_CODE (expected 3 / BLOCKED)" >&2
  exit 2
fi

echo "=== E03 smoke ==="
W13_RUNS_ROOT="$OUT/runs" python scripts/perf/w13.py e03 --mode smoke --replicas 1,2 --rate 5 --duration 5s

echo "=== analysis ==="
ANALYZE_CODE=0
python scripts/perf/w13_analyze.py --runs-root "$OUT/runs" --out-dir "$OUT/analysis" || ANALYZE_CODE=$?
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

  python scripts/perf/w13.py audit --out "$OUT/auditor"

and still requires a clean tree outside evidence/.
EOF
