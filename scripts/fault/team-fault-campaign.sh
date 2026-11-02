#!/usr/bin/env bash
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
out="$PWD/evidence/team/fault-campaign/outputs"
mkdir -p "$out"
git rev-parse HEAD > "$out/checkout-sha.txt"
git rev-parse HEAD^{tree} > "$out/checkout-tree.txt"
dotnet build RideHailing.Distributed.slnx -c Release > "$out/build.txt" 2>&1

run_test() {
  local project=$1 filter=$2 destination=$3
  mkdir -p "$destination"
  dotnet test "$project" -c Release --no-build --filter "$filter" \
    --logger trx --results-directory "$destination" > "$destination/test-output.txt" 2>&1
  # A zero-discovery exit must never qualify as a controlled pass.
  python3 - "$destination" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
files = list(pathlib.Path(sys.argv[1]).glob('*.trx'))
assert files, 'Missing TRX evidence'
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
executed = 0
for file in files:
    counters = ET.parse(file).find('t:ResultSummary/t:Counters', ns)
    assert counters is not None
    executed += int(counters.attrib['executed'])
    assert int(counters.attrib['failed']) == 0
    assert int(counters.attrib['executed']) == int(counters.attrib['total'])
assert executed > 0, 'No tests executed'
print(f'{executed} tests executed without failures or skips')
PY
}

for pass in 1 2 3 4 5; do
  run_test tests/Integration/RideHailing.TripService.IntegrationTests \
    'FullyQualifiedName~TripW11' "$out/pass-$pass/trip"
  run_test tests/Integration/RideHailing.Platform.IntegrationTests \
    'FullyQualifiedName~PublishAcceptedThenMarkLost|FullyQualifiedName~CommitBeforeAckRedelivery' \
    "$out/pass-$pass/platform"
  run_test tests/Integration/RideHailing.PaymentService.IntegrationTests \
    'FullyQualifiedName~SharedFaultAfterProviderCommit' "$out/pass-$pass/payment"
done

PASSES=5 bash scripts/fault/b-w11-full-campaign.sh
