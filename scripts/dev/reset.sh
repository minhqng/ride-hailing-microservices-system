#!/bin/bash
set -euo pipefail
# Destroys this project's local volumes only (compose project scope). Review before running.
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
docker compose down -v
