#!/bin/bash
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
# Per-invocation path prevents stale TRX files from satisfying this gate:
# epoch seconds, PID and a random suffix keep two runs in the same second apart.
TRX_DIR="TestResults/quick-$(date +%s)-$$-$RANDOM"
# Testcontainers-based integration tests need the Docker socket inside the SDK
# container, with SELinux label disabled so the socket stays connectable
# (--security-opt label=disable) and the container in the host docker group
# (--group-add <docker-gid>) so Testcontainers can start disposable
# PostgreSQL/Redis. Mounted only when present so socket-less hosts still
# build; infra-dependent tests then FAIL LOUD (never green-skip) because an
# authoritative proof must not pass when its infrastructure is absent.
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
  docker run --rm $SECURITY_OPT $GROUP_ADD -e "TRX_DIR=$TRX_DIR" -v "$REPO_ROOT:/work:z" -v /var/run/docker.sock:/var/run/docker.sock -w /work mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build --logger trx --results-directory "$TRX_DIR"'
else
  docker run --rm -e "TRX_DIR=$TRX_DIR" -v "$REPO_ROOT:/work:z" -w /work mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build --logger trx --results-directory "$TRX_DIR"'
fi

# The directory name is unique to this invocation, so any TRX inside it was
# written by this run. Refuse to evaluate a run that produced none instead of
# letting a stale or empty directory reach the gate.
if ! find "$TRX_DIR" -name '*.trx' -print -quit 2>/dev/null | grep -q .; then
  echo "quick.sh: no TRX files were produced under $TRX_DIR; refusing a false pass." >&2
  exit 1
fi

python3 scripts/test/assert-trx.py --results-dir "$TRX_DIR" --min-passed 1200 --min-files 13 --max-skipped 1 --allowed-skip "RideHailing.IdentityService.IntegrationTests.AuthNegativeTests.RejectedRequestsDoNotMutateAuthoritativeState" --required-suites-file scripts/test/required-suites.txt
