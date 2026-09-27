#!/bin/bash
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
docker run --rm -v "$REPO_ROOT:/work:z" -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build'
