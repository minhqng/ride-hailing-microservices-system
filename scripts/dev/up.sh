#!/bin/bash
set -euo pipefail
# Thin helper. Never overwrites .env. Copy .env.example to .env first.
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
if [ ! -f .env ]; then echo "Missing .env. Copy: cp .env.example .env"; exit 1; fi
docker compose up -d --build
