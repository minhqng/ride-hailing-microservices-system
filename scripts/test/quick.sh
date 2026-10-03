#!/bin/bash
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
# Testcontainers-based integration tests need the Docker socket inside the SDK
# container. Mounted only when present so socket-less hosts still build/test
# (infra tests then loud-skip via Skipped flag). Plain if/else (no bash arrays)
# for old bash (macOS 3.2) under `set -u`.
if [ -S /var/run/docker.sock ]; then
  docker run --rm -v "$REPO_ROOT:/work:z" -v /var/run/docker.sock:/var/run/docker.sock -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build'
else
  docker run --rm -v "$REPO_ROOT:/work:z" -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build'
fi
