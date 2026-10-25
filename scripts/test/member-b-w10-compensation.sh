#!/usr/bin/env bash
# Run from the repository root with .NET 10 and a disposable-capable Docker daemon.
set -euo pipefail
root="$(pwd)/evidence/member-b/w10-lifecycle/raw/phase2"
mkdir -p "$root"
# A mounted worktree can reference a host-only .git path. The caller records
# its Git diff and supplies the source head; these hashes identify tested inputs.
printf '%s\n' "${W10_SOURCE_HEAD:-not-supplied}" > "$root/source-head.txt"
find src tests scripts -type d \( -name bin -o -name obj \) -prune -o \
  -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.sh' -o -name '*.sql' \) -print0 \
  | sort -z | xargs -0 sha256sum > "$root/tested-inputs.sha256"
run() {
  local label=$1
  shift
  local result=0
  "$@" > "$root/$label.log" 2>&1 || result=$?
  tail -n 9 "$root/$label.log"
  echo "$label EXIT=$result"
  return "$result"
}
run build dotnet build RideHailing.Distributed.slnx -c Release --nologo
run compensation dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests -c Release --no-build --no-restore \
  --filter FullyQualifiedName~DriverCompensationTests --logger 'trx;LogFileName=compensation.trx' --results-directory "$root"
for entry in 'Unit Driver' 'Integration Driver' 'Unit Matching' 'Integration Matching' 'Unit Trip' 'Integration Trip'; do
  read -r layer service <<< "$entry"
  label="${service,,}-${layer,,}"
  run "$label" dotnet test "tests/$layer/RideHailing.${service}Service.${layer}Tests" -c Release --no-build --no-restore \
    --logger "trx;LogFileName=$label.trx" --results-directory "$root"
done
run platform-integration dotnet test tests/Integration/RideHailing.Platform.IntegrationTests -c Release --no-build --no-restore \
  --logger 'trx;LogFileName=platform-integration.trx' --results-directory "$root"
