#!/usr/bin/env bash
# Thin entry point for the W13 performance harness.
# Requires Python 3.11+ and Docker/Compose on PATH.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if command -v python3 >/dev/null 2>&1; then
  PYTHON=python3
elif command -v python >/dev/null 2>&1; then
  PYTHON=python
else
  echo "error: python 3 is required" >&2
  exit 2
fi
exec "$PYTHON" "$ROOT/scripts/perf/w13.py" "$@"
