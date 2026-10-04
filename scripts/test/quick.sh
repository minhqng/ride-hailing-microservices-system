#!/bin/bash
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
# Testcontainers-based integration tests need the Docker socket inside the SDK
# container, with SELinux label disabled so the socket stays connectable
# (--security-opt label=disable) and the container in the host docker group
# (--group-add <docker-gid>) so Testcontainers can start disposable
# PostgreSQL/Redis. Mounted only when present so socket-less hosts still
# build; infra-dependent tests then FAIL LOUD (never green-skip) because an
# authoritative W2/W3 proof must not pass when its infrastructure is absent.
# Plain if/else (no bash arrays) for old bash (macOS 3.2) under `set -u`.
DOCKER_GID="$(getent group docker 2>/dev/null | cut -d: -f3 || true)"
GROUP_ADD=""
if [ -n "${DOCKER_GID:-}" ]; then GROUP_ADD="--group-add $DOCKER_GID"; fi
# SELinux label disable is Linux-only; Docker Desktop (macOS/Windows) accepts
# the socket mount without it.
SECURITY_OPT=""
if [ "$(uname -s)" = "Linux" ]; then SECURITY_OPT="--security-opt label=disable"; fi
if [ -S /var/run/docker.sock ]; then
  # shellcheck disable=SC2086
  docker run --rm $SECURITY_OPT $GROUP_ADD -v "$REPO_ROOT:/work:z" -v /var/run/docker.sock:/var/run/docker.sock -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build'
else
  docker run --rm -v "$REPO_ROOT:/work:z" -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build'
fi
