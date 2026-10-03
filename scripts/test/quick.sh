#!/bin/bash
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
# Testcontainers-based integration tests need the Docker socket inside the SDK
# container. Mounted only when present so socket-less hosts still build/test
# (infra tests then loud-skip via DockerUnavailableException).
SOCKET_MOUNT=()
if [ -S /var/run/docker.sock ]; then
  SOCKET_MOUNT=(-v /var/run/docker.sock:/var/run/docker.sock)
fi
docker run --rm -v "$REPO_ROOT:/work:z" "${SOCKET_MOUNT[@]}" -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build'
