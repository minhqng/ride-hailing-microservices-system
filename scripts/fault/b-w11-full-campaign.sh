#!/usr/bin/env bash
# B-W11 Phase 2 acceptance runner: five consecutive controlled passes of
# REL-01, REL-02, CON-02, CON-04, REL-12, REL-13 and REL-14 on one source
# revision.
#
# Each pass gets a fresh container namespace and fresh test identities.
# A failed/zero-test scenario breaks the consecutive streak; all artifacts are
# retained under the pass directory. No user-owned containers are touched.
#
# Requirements: bash, docker, curl, node, dotnet (in PATH or DOTNET=...).
# Usage:
#   ./scripts/fault/b-w11-full-campaign.sh
#   PASSES=1 ./scripts/fault/b-w11-full-campaign.sh   # smoke only
set -Eeuo pipefail
export TESTCONTAINERS_RYUK_DISABLED=true

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)
DOTNET=${DOTNET:-dotnet}
PASSES=${PASSES:-5}
RUN_TS=$(date -u +%Y%m%dt%H%M%Sz)
OUT=${OUT:-$REPO_ROOT/evidence/member-b/w11-fault-proofs/raw/five-pass-$RUN_TS}
mkdir -p "$OUT"
LOG="$OUT/campaign.log"
: > "$LOG"

log() { echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*" | tee -a "$LOG"; }
die() { log "FAIL: $*"; exit 1; }

jget() { # file path
  node -e '
    const fs = require("fs");
    let v = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
    for (const k of process.argv[2].split(".")) v = v?.[k];
    if (v !== undefined && v !== null) process.stdout.write(String(v));
  ' "$1" "$2"
}

preflight() {
  for tool in docker curl node "$DOTNET"; do
    command -v "$tool" >/dev/null 2>&1 || die "missing required tool: $tool"
  done
  docker info >/dev/null 2>&1 || die "Docker is not available"
  local status
  status=$(git -C "$REPO_ROOT" status --porcelain)
  if [ -n "$status" ] && [ "${ALLOW_DIRTY:-0}" != "1" ]; then
    die "working tree is dirty; commit or set ALLOW_DIRTY=1 for a diagnostic run"
  fi
  log "source HEAD $(git -C "$REPO_ROOT" rev-parse HEAD) tree $(git -C "$REPO_ROOT" rev-parse HEAD^{tree})"
  log "source status '${status}'"
}

run_phase1() { # pass_dir [skip_build]
  local pass_dir=$1 skip=${2:-0}
  local phase_out="$pass_dir/rel01-rel02"
  mkdir -p "$phase_out"
  if [ "$skip" = "1" ]; then
    SKIP_BUILD=1 OUT="$phase_out" bash "$SCRIPT_DIR/b-w11-rel01-rel02.sh" \
      > "$pass_dir/rel01-rel02.log" 2>&1
  else
    OUT="$phase_out" bash "$SCRIPT_DIR/b-w11-rel01-rel02.sh" \
      > "$pass_dir/rel01-rel02.log" 2>&1
  fi
}

write_scenarios_header() {
  printf 'pass\tscenario\ttest\tresult\telapsed_s\tids\n' > "$OUT/scenarios.tsv"
}

record() { # pass scenario test result elapsed ids
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$5" "$6" \
    >> "$OUT/scenarios.tsv"
}

assert_passed_test() { # log test-fqn label
  local log_file=$1 test_name=$2 label=$3
  grep -q "Passed .*${test_name}" "$log_file" || die "$label did not report PASS"
  if grep -q "Failed .*${test_name}" "$log_file"; then
    die "$label reported FAIL"
  fi
}

assert_phase1() { # summary label
  local summary=$1 label=$2
  [ "$(jget "$summary" rel01.result)" = "PASS" ] || die "$label REL-01 not PASS"
  [ "$(jget "$summary" rel02.result)" = "PASS" ] || die "$label REL-02 not PASS"
}

phase1_ids() { # summary
  node -e '
    const fs = require("fs");
    const s = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
    process.stdout.write(
      `REL01 trip=${s.rel01.tripId} driver=${s.rel01.driverId} R=${s.rel01.reservationId}; ` +
      `REL02 trip=${s.rel02.tripId} driver=${s.rel02.driverId} R=${s.rel02.reservationId} ` +
      `pid=${s.rel02.killedPid}->${s.rel02.restartedPid} exit=${s.rel02.killedExitCode}`);
  ' "$1"
}

run_driver_tests() { # pass_dir
  local out=$1
  $DOTNET test \
    "$REPO_ROOT/tests/Integration/RideHailing.DriverService.IntegrationTests/RideHailing.DriverService.IntegrationTests.csproj" \
    -c Release --filter \
    "FullyQualifiedName~DriverCon02DeterministicTests|FullyQualifiedName~Con04_LateReleaseR1_ViaBroker|FullyQualifiedName~Stale_Release_After_ReReserve_Cannot_Free_Newer_Owner" \
    --logger "console;verbosity=detailed" > "$out/driver-integration.log" 2>&1
}

run_matching_tests() { # pass_dir
  local out=$1
  $DOTNET test \
    "$REPO_ROOT/tests/Integration/RideHailing.MatchingService.IntegrationTests/RideHailing.MatchingService.IntegrationTests.csproj" \
    -c Release --filter \
    "FullyQualifiedName~DriverProjectionStalenessHandlerTests|FullyQualifiedName~W11_Rel12_|FullyQualifiedName~W11_Rel13_|FullyQualifiedName~RedisOutageRecoveryTests" \
    --logger "console;verbosity=detailed" > "$out/matching-integration.log" 2>&1
}

preflight
SOURCE_HEAD=$(git -C "$REPO_ROOT" rev-parse HEAD)
SOURCE_TREE=$(git -C "$REPO_ROOT" rev-parse HEAD^{tree})
log "B-W11 five-pass campaign -> $OUT"
write_scenarios_header

log "Release build"
$DOTNET build "$REPO_ROOT/RideHailing.Distributed.slnx" -c Release \
  > "$OUT/release-build.log" 2>&1 || {
    tail -40 "$OUT/release-build.log" || true
    die "Release build failed"
  }

for pass in $(seq 1 "$PASSES"); do
  pass_dir="$OUT/pass-$pass"
  mkdir -p "$pass_dir"
  pass_start=$(date +%s)
  log "===== PASS $pass/$PASSES ====="
  log "PASS $pass start source $(git -C "$REPO_ROOT" rev-parse HEAD)"

  # REL-01 / REL-02 (real service containers; build once on pass 1)
  skip_build=1
  [ "$pass" -eq 1 ] && skip_build=0
  if ! run_phase1 "$pass_dir" "$skip_build"; then
    tail -40 "$pass_dir/rel01-rel02.log" | tee -a "$LOG"
    record "$pass" "REL-01/REL-02" "container-campaign" "FAIL" "$(( $(date +%s) - pass_start ))" "see rel01-rel02.log"
    die "pass $pass REL-01/REL-02 failed"
  fi
  phase_summary="$pass_dir/rel01-rel02/run-summary.json"
  [ -f "$phase_summary" ] || die "pass $pass missing $phase_summary"
  assert_phase1 "$phase_summary" "pass $pass"
  phase_elapsed=$(( $(date +%s) - pass_start ))
  if [ "$pass" -eq 1 ]; then
    export W11_DRIVER_IMAGE_ID=$(docker inspect -f '{{.Id}}' rhw11b-driver:local)
    export W11_MATCHING_IMAGE_ID=$(docker inspect -f '{{.Id}}' rhw11b-matching:local)
    log "images driver=$W11_DRIVER_IMAGE_ID matching=$W11_MATCHING_IMAGE_ID"
  fi
  record "$pass" "REL-01" "LostReserveResponse/container" "PASS" "$phase_elapsed" "$(phase1_ids "$phase_summary")"
  record "$pass" "REL-02" "MatchingSIGKILL/container" "PASS" "$phase_elapsed" "$(phase1_ids "$phase_summary")"
  log "PASS $pass REL-01/REL-02 PASS"

  # CON-02 (both schedules) + CON-04 (direct + real broker)
  driver_start=$(date +%s)
  if ! run_driver_tests "$pass_dir"; then
    tail -50 "$pass_dir/driver-integration.log" | tee -a "$LOG"
    record "$pass" "CON-02/CON-04" "driver-integration" "FAIL" "$(( $(date +%s) - driver_start ))" "see driver-integration.log"
    die "pass $pass Driver CON tests failed"
  fi
  assert_passed_test "$pass_dir/driver-integration.log" "OfflineWins_ThenReserveLoses_WithOnlyOfflineIntent" "CON-02 offline-wins"
  assert_passed_test "$pass_dir/driver-integration.log" "ReserveWins_ThenOfflineLoses_WithOnlyReservedIntent" "CON-02 reserve-wins"
  assert_passed_test "$pass_dir/driver-integration.log" "Stale_Release_After_ReReserve_Cannot_Free_Newer_Owner" "CON-04 direct"
  assert_passed_test "$pass_dir/driver-integration.log" "Con04_LateReleaseR1_ViaBroker" "CON-04 broker"
  driver_elapsed=$(( $(date +%s) - driver_start ))
  driver_ids=$(grep -h "W11 CON-" "$pass_dir/driver-integration.log" | tr '\n' ';' | sed 's/  */ /g')
  record "$pass" "CON-02" "both-schedules" "PASS" "$driver_elapsed" "$driver_ids"
  record "$pass" "CON-04" "direct+broker" "PASS" "$driver_elapsed" "$driver_ids"
  log "PASS $pass CON-02/CON-04 PASS"

  # REL-12/13 (handler + real broker) and REL-14 (real partition)
  matching_start=$(date +%s)
  if ! run_matching_tests "$pass_dir"; then
    tail -60 "$pass_dir/matching-integration.log" | tee -a "$LOG"
    record "$pass" "REL-12/13/14" "matching-integration" "FAIL" "$(( $(date +%s) - matching_start ))" "see matching-integration.log"
    die "pass $pass Matching REL tests failed"
  fi
  assert_passed_test "$pass_dir/matching-integration.log" "StaleStateEvent_CannotRegressVersionOrReenterGeo" "REL-12 handler"
  assert_passed_test "$pass_dir/matching-integration.log" "W11_Rel12_StaleState_ViaBroker" "REL-12 broker"
  assert_passed_test "$pass_dir/matching-integration.log" "StaleLocation_CannotRegressSequenceTimestampOrCoordinates" "REL-13 handler"
  assert_passed_test "$pass_dir/matching-integration.log" "W11_Rel13_StaleLocation_ViaBroker" "REL-13 broker"
  assert_passed_test "$pass_dir/matching-integration.log" "RedisPartition_DegradesCandidatePath" "REL-14"
  matching_elapsed=$(( $(date +%s) - matching_start ))
  matching_ids=$(grep -h "W11 REL-" "$pass_dir/matching-integration.log" | tr '\n' ';' | sed 's/  */ /g')
  record "$pass" "REL-12" "handler+broker" "PASS" "$matching_elapsed" "$matching_ids"
  record "$pass" "REL-13" "handler+broker" "PASS" "$matching_elapsed" "$matching_ids"
  record "$pass" "REL-14" "real-partition+recovery" "PASS" "$matching_elapsed" "$matching_ids"
  log "PASS $pass REL-12/REL-13/REL-14 PASS"

  pass_elapsed=$(( $(date +%s) - pass_start ))
  log "PASS $pass complete in ${pass_elapsed}s"
done

# Acceptance gate: every required scenario must be PASS in every pass.
required=(REL-01 REL-02 CON-02 CON-04 REL-12 REL-13 REL-14)
for scenario in "${required[@]}"; do
  for pass in $(seq 1 "$PASSES"); do
    ok=$(awk -F'\t' -v p="$pass" -v s="$scenario" \
      '$1==p && $2==s && $4=="PASS" {found=1} END {print found+0}' "$OUT/scenarios.tsv")
    [ "$ok" = "1" ] || die "acceptance matrix missing PASS for $scenario pass $pass"
  done
done

if [ "$(git -C "$REPO_ROOT" rev-parse HEAD)" != "$SOURCE_HEAD" ]; then
  die "source HEAD changed during the campaign; final acceptance run invalid"
fi

node -e '
  const fs = require("fs");
  const path = require("path");
  const out = process.argv[1];
  const source = process.argv[2];
  const tree = process.argv[3];
  const passes = Number(process.argv[4]);
  const rows = fs.readFileSync(path.join(out, "scenarios.tsv"), "utf8")
    .trim().split(/\r?\n/).slice(1).map(line => line.split("\t"));
  const matrix = {};
  for (const row of rows) {
    const [pass, scenario, test, result, elapsed, ids] = row;
    matrix[scenario] ??= { test, passes: [] };
    matrix[scenario].passes.push({ pass: Number(pass), result, elapsedSeconds: Number(elapsed), ids });
  }
  const summary = {
    runId: path.basename(out),
    timestampUtc: new Date().toISOString(),
    sourceHead: source,
    sourceTree: tree,
    passes,
    acceptance: passes === 5 ? "5/5 consecutive passes" : `smoke only (${passes} pass)`,
    driverImageId: process.env.W11_DRIVER_IMAGE_ID || null,
    matchingImageId: process.env.W11_MATCHING_IMAGE_ID || null,
    matrix,
    result: "PASS"
  };
  fs.writeFileSync(path.join(out, "five-pass-summary.json"), JSON.stringify(summary, null, 2));
' "$OUT" "$SOURCE_HEAD" "$SOURCE_TREE" "$PASSES"

log "FIVE-PASS PASS ($PASSES/$PASSES) — $OUT/five-pass-summary.json"
