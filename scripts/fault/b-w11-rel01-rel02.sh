#!/usr/bin/env bash
# B-W11 REL-01 / REL-02 deterministic fault campaign.
#
# Runs the real Driver and Matching service images, PostgreSQL, RabbitMQ and
# Redis in isolated Docker containers. REL-01 suppresses the Driver reserve
# response after the authoritative commit; REL-02 SIGKILLs the Matching
# container while it is blocked on that response, then restarts a NEW Matching
# container against the SAME database and broker.
#
# Requirements: bash, docker, curl, node. No local dotnet, psql or jq.
# Usage:
#   ./scripts/fault/b-w11-rel01-rel02.sh
#   SKIP_BUILD=1 ./scripts/fault/b-w11-rel01-rel02.sh   # reuse this run's images
#   KEEP_CONTAINERS=1 ...                              # leave containers for inspection
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)
RUN_TS=$(date -u +%Y%m%dt%H%M%Sz)
RUN_ID="rhw11b-${RUN_TS}-$$"
OUT=${OUT:-$REPO_ROOT/evidence/member-b/w11-fault-proofs/raw/$RUN_TS}
mkdir -p "$OUT/logs" "$OUT/snapshots"
LOG="$OUT/campaign.log"
: > "$LOG"

log() { echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*" | tee -a "$LOG"; }
die() { log "FAIL: $*"; exit 1; }

NET="$RUN_ID-net"
PG="$RUN_ID-pg"
RABBIT="$RUN_ID-rabbit"
REDIS="$RUN_ID-redis"
DRIVER="$RUN_ID-driver"
MATCH_REL01="$RUN_ID-match01"
MATCH_REL02_A="$RUN_ID-match02a"
MATCH_REL02_B="$RUN_ID-match02b"
IMAGE_DRIVER="rhw11b-driver:local"
IMAGE_MATCHING="rhw11b-matching:local"
TOKEN="$RUN_ID-token-$RANDOM$RANDOM"

DRIVER_URL=""
MATCH_REL01_URL=""
MATCH_REL02_A_URL=""
MATCH_REL02_B_URL=""
REL01_TRIP=""
REL01_D1=""
REL01_D2=""
REL01_RESERVATION=""
REL02_TRIP=""
REL02_D3=""
REL02_D4=""
REL02_RESERVATION=""
REL02_A_ID=""
REL02_A_PID=""
REL02_B_ID=""
REL02_B_PID=""

jget() { # path (reads JSON on stdin; prints scalar or empty)
  node -e '
    let s = "";
    process.stdin.on("data", d => s += d).on("end", () => {
      let v = JSON.parse(s);
      for (const k of process.argv[1].split(".")) v = v?.[k];
      if (v !== undefined && v !== null) process.stdout.write(String(v));
    });
  ' "$1"
}

uuid() { node -e 'process.stdout.write(crypto.randomUUID())'; }
dense_id() { printf '%s' "$1" | tr -d '-'; }

post_json() { # url outfile token body -> http code
  local url=$1 out=$2 token=$3 body=$4
  local args=(-sS -o "$out" -w '%{http_code}' -X POST "$url" -H 'content-type: application/json')
  [ -n "$token" ] && args+=(-H "X-Fault-Control-Token: $token")
  args+=(-d "$body")
  curl "${args[@]}"
}

get_json() { # url outfile token -> http code
  local url=$1 out=$2 token=$3
  local args=(-sS -o "$out" -w '%{http_code}' "$url")
  [ -n "$token" ] && args+=(-H "X-Fault-Control-Token: $token")
  curl "${args[@]}"
}

psqlq() { # role db sql
  docker exec "$PG" psql -U "$1" -d "$2" -X -A -t -v ON_ERROR_STOP=1 -c "$3"
}

psqlj() { # role db sql -> JSON array
  docker exec "$PG" psql -U "$1" -d "$2" -X -A -t -v ON_ERROR_STOP=1 -c \
    "select coalesce(json_agg(t), '[]'::json) from ($3) t"
}

assert_eq() {
  local got=$1 want=$2 message=$3
  [ "$got" = "$want" ] || die "$message: got '$got', want '$want'"
}

assert_json_eq() {
  local file=$1 path=$2 want=$3 message=$4
  local got
  got=$(jget "$path" < "$file")
  assert_eq "$got" "$want" "$message"
}

assert_driver_status() {
  driver_status_is "$1" "$2" || die "$3: expected $2"
}

assert_attempt_state() {
  attempt_state_is "$1" "$2" "$3" "$4" || die "$5: expected $4"
}

wait_for() { # timeout_seconds description command...
  local timeout=$1 description=$2
  shift 2
  local end=$((SECONDS + timeout))
  while [ "$SECONDS" -lt "$end" ]; do
    if "$@"; then
      return 0
    fi
    sleep 0.25
  done
  die "timed out after ${timeout}s waiting for $description"
}

http_ok() { [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$1" || true)" = "200" ]; }
pg_has_db() { docker exec "$PG" psql -U postgres -d postgres -tAc \
  "SELECT 1 FROM pg_database WHERE datname='$1'" 2>/dev/null | grep -q 1; }
rabbit_ready() { docker exec "$RABBIT" rabbitmq-diagnostics -q ping >/dev/null 2>&1; }
redis_ready() { docker exec "$REDIS" redis-cli ping 2>/dev/null | grep -q PONG; }

fault_snapshot() { # outfile
  local code
  code=$(get_json "$DRIVER_URL/internal/test/faults" "$1" "$TOKEN")
  [ "$code" = "200" ] || return 1
}
fault_reached() {
  fault_snapshot "$OUT/snapshots/fault-poll.json" || return 1
  local n
  n=$(jget reachedCount < "$OUT/snapshots/fault-poll.json")
  [ -n "$n" ] && [ "$n" -ge 1 ]
}
fault_reached_above() { # baseline
  fault_snapshot "$OUT/snapshots/fault-poll.json" || return 1
  local n
  n=$(jget reachedCount < "$OUT/snapshots/fault-poll.json")
  [ -n "$n" ] && [ "$n" -gt "$1" ]
}
fault_pending_zero() {
  fault_snapshot "$OUT/snapshots/fault-poll.json" || return 1
  [ "$(jget pendingCount < "$OUT/snapshots/fault-poll.json")" = "0" ]
}

attempt_exists() { local s; s=$(psqlq matching_app matching_db \
  "select state from matching_attempts where trip_id='$1' and matching_round=$2 and ordinal=$3"); [ -n "$s" ]; }
attempt_state_is() { local s; s=$(psqlq matching_app matching_db \
  "select state from matching_attempts where trip_id='$1' and matching_round=$2 and ordinal=$3"); [ "$s" = "$4" ]; }
attempt_inflight() { attempt_state_is "$1" "$2" "$3" "IN_FLIGHT"; }
attempt_unknown() { attempt_state_is "$1" "$2" "$3" "UNKNOWN"; }
attempt_fenced() { local v; v=$(psqlq matching_app matching_db \
  "select coalesce(max_dispatched_version::text,'') from matching_attempts where trip_id='$1' and matching_round=$2 and ordinal=$3"); [ -n "$v" ]; }

driver_status_is() { [ "$(psqlq driver_app driver_db \
  "select status from drivers where driver_id='$1'")" = "$2" ]; }

driver_version() { psqlq driver_app driver_db "select version from drivers where driver_id='$1'"; }
driver_reservation() { psqlq driver_app driver_db "select reservation_id from drivers where driver_id='$1'"; }
driver_outbox_count() { psqlq driver_app driver_db \
  "select count(*) from outbox_messages where message_type='DriverReservedV1' and aggregate_id='$(dense_id "$1")'"; }
driver_outbox_reservation() { psqlq driver_app driver_db \
  "select min(payload_json->>'reservationId') from outbox_messages where message_type='DriverReservedV1' and aggregate_id='$(dense_id "$1")'"; }
driver_outbox_round() { psqlq driver_app driver_db \
  "select min(payload_json->>'matchingRound') from outbox_messages where message_type='DriverReservedV1' and aggregate_id='$(dense_id "$1")'"; }
matching_failed_count() { psqlq matching_app matching_db \
  "select count(*) from outbox_messages where message_type='MatchingFailedV1' and payload_json->>'tripId'='$1'"; }
attempt_count() { psqlq matching_app matching_db \
  "select count(*) from matching_attempts where trip_id='$1' and matching_round=$2"; }
round_status() { psqlq matching_app matching_db \
  "select status from matching_rounds where trip_id='$1' and matching_round=$2"; }
round_selected_reservation() { psqlq matching_app matching_db \
  "select selected_reservation_id from matching_rounds where trip_id='$1' and matching_round=$2"; }
round_result_message() { psqlq matching_app matching_db \
  "select coalesce(result_message_id::text,'') from matching_rounds where trip_id='$1' and matching_round=$2"; }

capture_driver_state() { # outfile driverId...
  local out=$1
  shift
  local ids=""
  for id in "$@"; do ids+="'$id',"; done
  ids=${ids%,}
  psqlj driver_app driver_db \
    "select driver_id,status,version,reserved_trip_id,reservation_id,reserved_round,updated_at
     from drivers where driver_id in ($ids) order by driver_id" > "$out"
}

capture_trip_state() { # outfile trip round driver...
  local out=$1 trip=$2 round=$3
  shift 3
  local ids=""
  for id in "$@"; do ids+="'$(dense_id "$id")',"; done
  ids=${ids%,}
  psqlj matching_app matching_db \
    "select trip_id,matching_round,ordinal,driver_id,reservation_id,state,last_error_code,
            max_dispatched_version,updated_at
     from matching_attempts where trip_id='$trip' and matching_round=$round order by ordinal" > "$out"
  psqlj matching_app matching_db \
    "select trip_id,matching_round,status,selected_driver_id,selected_reservation_id,
            result_message_id,updated_at
     from matching_rounds where trip_id='$trip' and matching_round=$round" > "${out%.json}-round.json"
  psqlj driver_app driver_db \
    "select message_id,message_type,status,payload_json->>'reservationId' as reservation_id,
            payload_json->>'matchingRound' as matching_round,payload_json->>'tripId' as trip_id,
            attempt_count,published_at
     from outbox_messages where aggregate_id in ($ids) order by occurred_at" > "${out%.json}-driver-outbox.json"
  psqlj matching_app matching_db \
    "select message_id,message_type,status,payload_json->>'tripId' as trip_id,attempt_count,published_at
     from outbox_messages where payload_json->>'tripId'='$trip' order by occurred_at" > "${out%.json}-matching-outbox.json"
}

capture_container_identity() { # name outfile
  {
    echo "name=$(docker inspect -f '{{.Name}}' "$1")"
    echo "id=$(docker inspect -f '{{.Id}}' "$1")"
    echo "pid=$(docker inspect -f '{{.State.Pid}}' "$1")"
    echo "started=$(docker inspect -f '{{.State.StartedAt}}' "$1")"
    echo "running=$(docker inspect -f '{{.State.Running}}' "$1")"
    echo "exit_code=$(docker inspect -f '{{.State.ExitCode}}' "$1")"
    echo "oom_killed=$(docker inspect -f '{{.State.OOMKilled}}' "$1")"
  } > "$2"
}

create_driver() {
  local user body id state
  user=$(uuid)
  body=$(curl -sS -X POST "$DRIVER_URL/drivers" -H 'content-type: application/json' \
    -d "{\"userId\":\"$user\"}")
  id=$(printf '%s' "$body" | jget driverId)
  [ -n "$id" ] || die "driver create failed: $body"
  state=$(curl -sS -X POST "$DRIVER_URL/drivers/$id/online" -H 'content-type: application/json' \
    -d '{"expectedVersion":1}')
  [ "$(printf '%s' "$state" | jget status)" = "AVAILABLE" ] || die "driver online failed: $state"
  printf '%s' "$id"
}

start_reserve_request() { # out-prefix trip d1 d2
  local out=$1 trip=$2 d1=$3 d2=$4
  (
    # REL-02 deliberately kills the server mid-request: the client failure is
    # expected evidence, not a campaign error.
    trap - ERR
    set +e
    curl -sS -o "$out.json" -w '%{http_code}' -X POST \
      "$5/internal/test/matching/reserve" \
      -H 'content-type: application/json' \
      -H "X-Fault-Control-Token: $TOKEN" \
      -d "{\"tripId\":\"$trip\",\"matchingRound\":1,\"driverIds\":[\"$d1\",\"$d2\"],\"inputMessageId\":\"$trip\"}" \
      > "$out.code" 2> "$out.err"
    echo $? > "$out.exit"
  ) &
  RESERVE_CURL_PID=$!
}

build_images() {
  if [ "${SKIP_BUILD:-0}" = "1" ]; then
    log "SKIP_BUILD=1: reusing $IMAGE_DRIVER and $IMAGE_MATCHING"
    return
  fi

  log "Building Driver image"
  if ! docker build -f "$REPO_ROOT/src/Services/Driver/RideHailing.DriverService/Dockerfile" \
      -t "$IMAGE_DRIVER" "$REPO_ROOT" > "$OUT/logs/build-driver.log" 2>&1; then
    tail -40 "$OUT/logs/build-driver.log" || true
    die "Driver image build failed"
  fi
  log "Building Matching image"
  if ! docker build -f "$REPO_ROOT/src/Services/Matching/RideHailing.MatchingService/Dockerfile" \
      -t "$IMAGE_MATCHING" "$REPO_ROOT" > "$OUT/logs/build-matching.log" 2>&1; then
    tail -40 "$OUT/logs/build-matching.log" || true
    die "Matching image build failed"
  fi
}

start_infrastructure() {
  docker network create "$NET" >/dev/null
  log "Starting PostgreSQL, RabbitMQ and Redis in $NET"
  docker run -d --name "$PG" --network "$NET" \
    -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres_dev_pw -e POSTGRES_DB=postgres \
    -e IDENTITY_DB_PASSWORD=identity_dev_pw -e DRIVER_DB_PASSWORD=driver_dev_pw \
    -e TRIP_DB_PASSWORD=trip_dev_pw -e MATCHING_DB_PASSWORD=matching_dev_pw \
    -e PAYMENT_DB_PASSWORD=payment_dev_pw -e NOTIFICATION_DB_PASSWORD=notification_dev_pw \
    -v "$REPO_ROOT/deploy/postgres/init:/docker-entrypoint-initdb.d:ro" \
    postgres:18.6 >/dev/null
  docker run -d --name "$RABBIT" --network "$NET" \
    -e RABBITMQ_DEFAULT_USER=ride_app -e RABBITMQ_DEFAULT_PASS=ride_dev_pw \
    rabbitmq:4.3.6-management >/dev/null
  docker run -d --name "$REDIS" --network "$NET" redis:8.10.2 >/dev/null

  wait_for 90 "PostgreSQL databases" pg_has_db driver_db
  wait_for 60 "Matching database" pg_has_db matching_db
  wait_for 90 "RabbitMQ" rabbit_ready
  wait_for 30 "Redis" redis_ready
}

start_driver() {
  log "Starting Driver service with the W11 fault point enabled"
  docker run -d --name "$DRIVER" --network "$NET" -p 127.0.0.1::8080 \
    -e ASPNETCORE_ENVIRONMENT=Testing \
    -e ASPNETCORE_URLS=http://+:8080 \
    -e "ConnectionStrings__Postgres=Host=$PG;Database=driver_db;Username=driver_app;Password=driver_dev_pw" \
    -e "RabbitMq__Host=$RABBIT" -e RabbitMq__Username=ride_app -e RabbitMq__Password=ride_dev_pw \
    -e "RIDE_FAULTPOINTS=Driver.AfterReserveCommitBeforeResponse" \
    -e "RIDE_FAULT_MAX_HOLD_SECONDS=90" \
    -e "RIDE_FAULT_CONTROL_TOKEN=$TOKEN" \
    "$IMAGE_DRIVER" >/dev/null
  DRIVER_PORT=$(docker port "$DRIVER" 8080/tcp | head -n1 | sed 's/.*://')
  DRIVER_URL="http://127.0.0.1:$DRIVER_PORT"
  wait_for 90 "Driver health" http_ok "$DRIVER_URL/health/live"
  fault_snapshot "$OUT/snapshots/fault-enabled.json" || die "fault snapshot unavailable"
  assert_json_eq "$OUT/snapshots/fault-enabled.json" "enabled" "true" \
    "Driver fault seam is enabled in Testing"
  log "Driver ready at $DRIVER_URL"
}

start_matching() { # name driver_timeout_seconds -> host url on stdout
  local name=$1 timeout=$2 port
  docker run -d --name "$name" --network "$NET" -p 127.0.0.1::8080 \
    -e ASPNETCORE_ENVIRONMENT=Testing \
    -e ASPNETCORE_URLS=http://+:8080 \
    -e "ConnectionStrings__Postgres=Host=$PG;Database=matching_db;Username=matching_app;Password=matching_dev_pw" \
    -e "Redis__ConnectionString=$REDIS:6379" \
    -e "RabbitMq__Host=$RABBIT" -e RabbitMq__Username=ride_app -e RabbitMq__Password=ride_dev_pw \
    -e "Services__DriverBaseUrl=http://$DRIVER:8080" \
    -e "DriverClient__TimeoutSeconds=$timeout" \
    -e "RIDE_FAULT_CONTROL_TOKEN=$TOKEN" \
    "$IMAGE_MATCHING" >/dev/null
  port=$(docker port "$name" 8080/tcp | head -n1 | sed 's/.*://')
  wait_for 90 "Matching $name health" http_ok "http://127.0.0.1:$port/health/live"
  printf '%s' "http://127.0.0.1:$port"
}

release_fault() {
  local code
  code=$(post_json \
    "$DRIVER_URL/internal/test/faults/Driver.AfterReserveCommitBeforeResponse/release" \
    "$OUT/logs/fault-release.json" "$TOKEN" '{}')
  assert_eq "$code" "200" "fault release HTTP status"
  assert_json_eq "$OUT/logs/fault-release.json" "released" "true" "fault release"
  wait_for 10 "fault pending queue to drain" fault_pending_zero
}

capture_all_logs() {
  for name in "$DRIVER" "$MATCH_REL01" "$MATCH_REL02_A" "$MATCH_REL02_B" "$RABBIT" "$PG" "$REDIS"; do
    if docker inspect "$name" >/dev/null 2>&1; then
      docker logs "$name" > "$OUT/logs/$name.log" 2>&1 || true
      # State only: a full inspect would copy container env (tokens/passwords)
      # into the evidence tree.
      docker inspect --format '{{json .State}}' "$name" \
        > "$OUT/logs/$name.state.json" 2>&1 || true
    fi
  done
}

cleanup() {
  capture_all_logs || true
  if [ "${KEEP_CONTAINERS:-0}" = "1" ]; then
    log "KEEP_CONTAINERS=1: leaving containers and network in place"
    return
  fi
  docker rm -f "$MATCH_REL02_B" "$MATCH_REL02_A" "$MATCH_REL01" "$DRIVER" \
    "$RABBIT" "$REDIS" "$PG" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}

on_exit() {
  local rc=$?
  log "campaign exiting rc=$rc"
  cleanup
  exit "$rc"
}
trap on_exit EXIT
trap 'log "ERROR at line $LINENO: $BASH_COMMAND"' ERR

# ---------------------------------------------------------------------------
# Campaign
# ---------------------------------------------------------------------------
log "B-W11 REL-01/REL-02 campaign $RUN_ID"
log "source HEAD $(git -C "$REPO_ROOT" rev-parse HEAD)"
log "evidence $OUT"

build_images
start_infrastructure
start_driver

log "REL-01: create isolated drivers"
REL01_D1=$(create_driver)
REL01_D2=$(create_driver)
REL01_D1_VERSION_BEFORE=$(driver_version "$REL01_D1")
REL01_D2_VERSION_BEFORE=$(driver_version "$REL01_D2")
REL01_TRIP=$(uuid)
log "REL-01 trip=$REL01_TRIP drivers=$REL01_D1,$REL01_D2"

MATCH_REL01_URL=$(start_matching "$MATCH_REL01" 3)
log "REL-01 Matching ready at $MATCH_REL01_URL"

# Replace the placeholder URL used by start_reserve_request's 5th argument.
start_reserve_request "$OUT/logs/rel01-reserve" "$REL01_TRIP" "$REL01_D1" "$REL01_D2" "$MATCH_REL01_URL"
wait_for 30 "REL-01 Driver fault point reached" fault_reached

capture_driver_state "$OUT/snapshots/rel01-before-release-drivers.json" "$REL01_D1" "$REL01_D2"
capture_trip_state "$OUT/snapshots/rel01-before-release-attempts.json" "$REL01_TRIP" 1 "$REL01_D1" "$REL01_D2"
fault_snapshot "$OUT/snapshots/rel01-before-release-fault.json" || die "REL-01 fault snapshot unavailable"

REL01_DRIVER_VERSION=$(driver_version "$REL01_D1")
REL01_RESERVATION=$(driver_reservation "$REL01_D1")
assert_driver_status "$REL01_D1" RESERVED "REL-01 Driver committed RESERVED"
assert_eq "$(driver_outbox_count "$REL01_D1")" "1" "REL-01 exactly one DriverReservedV1 intent"
assert_eq "$(driver_outbox_reservation "$REL01_D1")" "$REL01_RESERVATION" "REL-01 Outbox reservation identity"
assert_eq "$(driver_outbox_round "$REL01_D1")" "1" "REL-01 Outbox matching round"
wait_for 10 "REL-01 attempt fence" attempt_fenced "$REL01_TRIP" 1 0
wait_for 10 "REL-01 attempt unresolved while response is held" \
  attempt_exists "$REL01_TRIP" 1 0

# Wait for the Matching HTTP call to time out while the Driver still holds.
wait "$RESERVE_CURL_PID" || true
REL01_HTTP=$(cat "$OUT/logs/rel01-reserve.code" 2>/dev/null || true)
REL01_CURL_EXIT=$(cat "$OUT/logs/rel01-reserve.exit" 2>/dev/null || true)
assert_eq "$REL01_HTTP" "200" "REL-01 trigger HTTP status"
assert_json_eq "$OUT/logs/rel01-reserve.json" "outcome" "BlockedOnUnknown" "REL-01 matching outcome"
wait_for 10 "REL-01 attempt UNKNOWN" attempt_unknown "$REL01_TRIP" 1 0

capture_driver_state "$OUT/snapshots/rel01-unknown-drivers.json" "$REL01_D1" "$REL01_D2"
capture_trip_state "$OUT/snapshots/rel01-unknown-attempts.json" "$REL01_TRIP" 1 "$REL01_D1" "$REL01_D2"
assert_eq "$(attempt_count "$REL01_TRIP" 1)" "1" "REL-01 exactly one attempt row"
assert_attempt_state "$REL01_TRIP" 1 0 UNKNOWN "REL-01 attempt UNKNOWN"
assert_eq "$(psqlq matching_app matching_db \
  "select last_error_code from matching_attempts where trip_id='$REL01_TRIP' and matching_round=1 and ordinal=0")" \
  "RESERVE_TRANSPORT" "REL-01 transport uncertainty code"
assert_eq "$(round_status "$REL01_TRIP" 1)" "PROCESSING" "REL-01 round not terminal"
assert_eq "$(round_result_message "$REL01_TRIP" 1)" "" "REL-01 no terminal result identity"
assert_eq "$(matching_failed_count "$REL01_TRIP")" "0" "REL-01 no false MatchingFailedV1"
assert_driver_status "$REL01_D2" AVAILABLE "REL-01 next Driver untouched"
assert_eq "$(driver_version "$REL01_D2")" "$REL01_D2_VERSION_BEFORE" "REL-01 next Driver version"
assert_eq "$(driver_outbox_count "$REL01_D1")" "1" "REL-01 Outbox not duplicated by timeout"
log "REL-01 timeout observed: HTTP=$REL01_HTTP curl_exit=$REL01_CURL_EXIT reservation=$REL01_RESERVATION"

release_fault

REL01_RECONCILE_CODE=$(post_json \
  "$MATCH_REL01_URL/internal/test/matching/attempts/$REL01_TRIP/1/0/reconcile" \
  "$OUT/logs/rel01-reconcile.json" "$TOKEN" '{}')
assert_eq "$REL01_RECONCILE_CODE" "200" "REL-01 reconcile HTTP status"
assert_json_eq "$OUT/logs/rel01-reconcile.json" "outcome" "Reserved" "REL-01 reconcile outcome"
assert_json_eq "$OUT/logs/rel01-reconcile.json" "reservationId" "$REL01_RESERVATION" \
  "REL-01 reconcile reused the same reservation"

# Exact replay of reconcile: no new identity, version or Outbox effect.
REL01_REPLAY_CODE=$(post_json \
  "$MATCH_REL01_URL/internal/test/matching/attempts/$REL01_TRIP/1/0/reconcile" \
  "$OUT/logs/rel01-reconcile-replay.json" "$TOKEN" '{}')
assert_eq "$REL01_REPLAY_CODE" "200" "REL-01 replay reconcile HTTP status"
assert_json_eq "$OUT/logs/rel01-reconcile-replay.json" "outcome" "Reserved" "REL-01 replay outcome"
assert_json_eq "$OUT/logs/rel01-reconcile-replay.json" "reservationId" "$REL01_RESERVATION" \
  "REL-01 replay reservation identity"

capture_driver_state "$OUT/snapshots/rel01-final-drivers.json" "$REL01_D1" "$REL01_D2"
capture_trip_state "$OUT/snapshots/rel01-final-attempts.json" "$REL01_TRIP" 1 "$REL01_D1" "$REL01_D2"
assert_eq "$(attempt_count "$REL01_TRIP" 1)" "1" "REL-01 final one attempt"
assert_attempt_state "$REL01_TRIP" 1 0 RESERVED "REL-01 final attempt RESERVED"
assert_eq "$(psqlq matching_app matching_db \
  "select reservation_id from matching_attempts where trip_id='$REL01_TRIP' and matching_round=1 and ordinal=0")" \
  "$REL01_RESERVATION" "REL-01 final attempt reservation identity"
assert_eq "$(round_status "$REL01_TRIP" 1)" "RESERVED" "REL-01 final round RESERVED"
assert_eq "$(round_selected_reservation "$REL01_TRIP" 1)" "$REL01_RESERVATION" \
  "REL-01 round selected reservation"
assert_driver_status "$REL01_D1" RESERVED "REL-01 final Driver RESERVED"
assert_eq "$(driver_reservation "$REL01_D1")" "$REL01_RESERVATION" "REL-01 final Driver reservation"
assert_eq "$(driver_version "$REL01_D1")" "$REL01_DRIVER_VERSION" "REL-01 Driver version stable"
assert_eq "$(driver_outbox_count "$REL01_D1")" "1" "REL-01 final single Outbox intent"
assert_eq "$(driver_outbox_reservation "$REL01_D1")" "$REL01_RESERVATION" "REL-01 final Outbox identity"
assert_eq "$(matching_failed_count "$REL01_TRIP")" "0" "REL-01 final no MatchingFailedV1"
log "REL-01 PASS: reservation=$REL01_RESERVATION unchanged across timeout and reconcile"

# ---------------------------------------------------------------------------
# REL-02: actual Matching process kill after the Driver commit.
# ---------------------------------------------------------------------------
docker stop "$MATCH_REL01" >/dev/null
log "REL-02: create isolated drivers"
REL02_D3=$(create_driver)
REL02_D4=$(create_driver)
REL02_D3_VERSION_BEFORE=$(driver_version "$REL02_D3")
REL02_D4_VERSION_BEFORE=$(driver_version "$REL02_D4")
REL02_TRIP=$(uuid)
log "REL-02 trip=$REL02_TRIP drivers=$REL02_D3,$REL02_D4"

MATCH_REL02_A_URL=$(start_matching "$MATCH_REL02_A" 60)
capture_container_identity "$MATCH_REL02_A" "$OUT/snapshots/rel02-process-before-kill.txt"
REL02_A_ID=$(docker inspect -f '{{.Id}}' "$MATCH_REL02_A")
REL02_A_PID=$(docker inspect -f '{{.State.Pid}}' "$MATCH_REL02_A")
log "REL-02 Matching-A $REL02_A_ID pid=$REL02_A_PID at $MATCH_REL02_A_URL"

fault_snapshot "$OUT/snapshots/rel02-fault-baseline.json" || die "REL-02 fault baseline unavailable"
REL02_FAULT_BASELINE=$(jget reachedCount < "$OUT/snapshots/rel02-fault-baseline.json")
[ -n "$REL02_FAULT_BASELINE" ] || die "REL-02 fault baseline count missing"
start_reserve_request "$OUT/logs/rel02-reserve" "$REL02_TRIP" "$REL02_D3" "$REL02_D4" "$MATCH_REL02_A_URL"
wait_for 30 "REL-02 new Driver fault hit" fault_reached_above "$REL02_FAULT_BASELINE"
wait_for 10 "REL-02 durable IN_FLIGHT attempt" attempt_inflight "$REL02_TRIP" 1 0
wait_for 10 "REL-02 dispatch fence" attempt_fenced "$REL02_TRIP" 1 0
fault_snapshot "$OUT/snapshots/rel02-before-crash-fault.json" || die "REL-02 fault snapshot unavailable"
assert_json_eq "$OUT/snapshots/rel02-before-crash-fault.json" "pendingCount" "1" \
  "REL-02 exactly one held post-commit response"
[ -n "$(jget lastReachedAtUtc < "$OUT/snapshots/rel02-before-crash-fault.json")" ] \
  || die "REL-02 fault reached timestamp missing"

REL02_DRIVER_VERSION=$(driver_version "$REL02_D3")
REL02_RESERVATION=$(driver_reservation "$REL02_D3")
assert_driver_status "$REL02_D3" RESERVED "REL-02 Driver committed RESERVED"
assert_eq "$(driver_outbox_count "$REL02_D3")" "1" "REL-02 Driver has one Outbox intent"
assert_attempt_state "$REL02_TRIP" 1 0 IN_FLIGHT \
  "REL-02 Matching has durable IN_FLIGHT before kill"

capture_driver_state "$OUT/snapshots/rel02-before-crash-drivers.json" "$REL02_D3" "$REL02_D4"
capture_trip_state "$OUT/snapshots/rel02-before-crash-attempts.json" "$REL02_TRIP" 1 "$REL02_D3" "$REL02_D4"
log "REL-02 pre-crash: reservation=$REL02_RESERVATION driverVersion=$REL02_DRIVER_VERSION attempt=IN_FLIGHT state=RESERVED"

log "REL-02 SIGKILL Matching container $MATCH_REL02_A"
docker kill --signal=KILL "$MATCH_REL02_A" >/dev/null
for _ in $(seq 1 40); do
  [ "$(docker inspect -f '{{.State.Running}}' "$MATCH_REL02_A")" = "false" ] && break
  sleep 0.25
done
[ "$(docker inspect -f '{{.State.Running}}' "$MATCH_REL02_A")" = "false" ] \
  || die "REL-02 Matching did not stop after SIGKILL"
REL02_A_EXIT=$(docker wait "$MATCH_REL02_A" 2>/dev/null) || REL02_A_EXIT=$?
REL02_A_RUNNING=$(docker inspect -f '{{.State.Running}}' "$MATCH_REL02_A")
REL02_A_EXIT_INSPECT=$(docker inspect -f '{{.State.ExitCode}}' "$MATCH_REL02_A")
REL02_A_FINISHED=$(docker inspect -f '{{.State.FinishedAt}}' "$MATCH_REL02_A")
capture_container_identity "$MATCH_REL02_A" "$OUT/snapshots/rel02-process-after-kill.txt"
assert_eq "$REL02_A_EXIT" "137" "REL-02 original Matching process exit"
assert_eq "$REL02_A_RUNNING" "false" "REL-02 original Matching process is not running"
assert_eq "$REL02_A_EXIT_INSPECT" "137" "REL-02 original container inspected exit"
log "REL-02 original Matching PID $REL02_A_PID exited 137 at $REL02_A_FINISHED"

wait "$RESERVE_CURL_PID" || true
REL02_HTTP=$(cat "$OUT/logs/rel02-reserve.code" 2>/dev/null || true)
REL02_CURL_EXIT=$(cat "$OUT/logs/rel02-reserve.exit" 2>/dev/null || true)
REL02_PORT_A=$(printf '%s' "$MATCH_REL02_A_URL" | sed 's/.*://')
REL02_REFUSED=$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 \
  "http://127.0.0.1:$REL02_PORT_A/health/live" || true)
[ "$REL02_HTTP" != "200" ] || die "REL-02 reserve request survived the Matching kill"
[ "$REL02_REFUSED" != "200" ] || die "REL-02 killed Matching still answered health"
log "REL-02 reserve request failed after kill: http='$REL02_HTTP' curl_exit='$REL02_CURL_EXIT' health='$REL02_REFUSED'"

release_fault

capture_driver_state "$OUT/snapshots/rel02-after-kill-drivers.json" "$REL02_D3" "$REL02_D4"
capture_trip_state "$OUT/snapshots/rel02-after-kill-attempts.json" "$REL02_TRIP" 1 "$REL02_D3" "$REL02_D4"
assert_driver_status "$REL02_D3" RESERVED "REL-02 Driver still committed after kill"
assert_eq "$(driver_reservation "$REL02_D3")" "$REL02_RESERVATION" "REL-02 Driver identity after kill"
assert_eq "$(driver_outbox_count "$REL02_D3")" "1" "REL-02 Outbox still single after kill"
assert_attempt_state "$REL02_TRIP" 1 0 IN_FLIGHT \
  "REL-02 failed process left IN_FLIGHT durable"
assert_eq "$(round_status "$REL02_TRIP" 1)" "PROCESSING" "REL-02 round left PROCESSING"
assert_driver_status "$REL02_D4" AVAILABLE "REL-02 next Driver never touched"

MATCH_REL02_B_URL=$(start_matching "$MATCH_REL02_B" 5)
capture_container_identity "$MATCH_REL02_B" "$OUT/snapshots/rel02-process-after-restart.txt"
REL02_B_ID=$(docker inspect -f '{{.Id}}' "$MATCH_REL02_B")
REL02_B_PID=$(docker inspect -f '{{.State.Pid}}' "$MATCH_REL02_B")
[ "$REL02_B_ID" != "$REL02_A_ID" ] || die "REL-02 restarted container identity did not change"
[ "$REL02_B_PID" != "$REL02_A_PID" ] || die "REL-02 restarted process identity did not change"
log "REL-02 Matching-B $REL02_B_ID pid=$REL02_B_PID at $MATCH_REL02_B_URL"

REL02_RECONCILE_CODE=$(post_json \
  "$MATCH_REL02_B_URL/internal/test/matching/attempts/$REL02_TRIP/1/0/reconcile" \
  "$OUT/logs/rel02-reconcile.json" "$TOKEN" '{}')
assert_eq "$REL02_RECONCILE_CODE" "200" "REL-02 reconcile HTTP status"
assert_json_eq "$OUT/logs/rel02-reconcile.json" "outcome" "Reserved" "REL-02 reconcile outcome"
assert_json_eq "$OUT/logs/rel02-reconcile.json" "reservationId" "$REL02_RESERVATION" \
  "REL-02 restarted process used the persisted reservation identity"

REL02_REPLAY_CODE=$(post_json \
  "$MATCH_REL02_B_URL/internal/test/matching/attempts/$REL02_TRIP/1/0/reconcile" \
  "$OUT/logs/rel02-reconcile-replay.json" "$TOKEN" '{}')
assert_eq "$REL02_REPLAY_CODE" "200" "REL-02 replay HTTP status"
assert_json_eq "$OUT/logs/rel02-reconcile-replay.json" "reservationId" "$REL02_RESERVATION" \
  "REL-02 replay reservation identity"

capture_driver_state "$OUT/snapshots/rel02-final-drivers.json" "$REL02_D3" "$REL02_D4"
capture_trip_state "$OUT/snapshots/rel02-final-attempts.json" "$REL02_TRIP" 1 "$REL02_D3" "$REL02_D4"
assert_eq "$(attempt_count "$REL02_TRIP" 1)" "1" "REL-02 final exactly one attempt"
assert_attempt_state "$REL02_TRIP" 1 0 RESERVED "REL-02 final attempt RESERVED"
assert_eq "$(psqlq matching_app matching_db \
  "select reservation_id from matching_attempts where trip_id='$REL02_TRIP' and matching_round=1 and ordinal=0")" \
  "$REL02_RESERVATION" "REL-02 final attempt identity"
assert_eq "$(round_status "$REL02_TRIP" 1)" "RESERVED" "REL-02 final round RESERVED"
assert_eq "$(round_selected_reservation "$REL02_TRIP" 1)" "$REL02_RESERVATION" \
  "REL-02 round selected reservation"
assert_driver_status "$REL02_D3" RESERVED "REL-02 final Driver RESERVED"
assert_eq "$(driver_reservation "$REL02_D3")" "$REL02_RESERVATION" "REL-02 final Driver reservation"
assert_eq "$(driver_version "$REL02_D3")" "$REL02_DRIVER_VERSION" "REL-02 Driver version stable"
assert_eq "$(driver_outbox_count "$REL02_D3")" "1" "REL-02 final single Outbox intent"
assert_eq "$(driver_outbox_reservation "$REL02_D3")" "$REL02_RESERVATION" "REL-02 final Outbox identity"
assert_eq "$(matching_failed_count "$REL02_TRIP")" "0" "REL-02 no false MatchingFailedV1"
assert_driver_status "$REL02_D4" AVAILABLE "REL-02 final next Driver untouched"
assert_eq "$(driver_version "$REL02_D4")" "$REL02_D4_VERSION_BEFORE" "REL-02 final next Driver version"
log "REL-02 PASS: old PID $REL02_A_PID exited 137; new PID $REL02_B_PID reconciled R=$REL02_RESERVATION"

# Curated evidence at the evidence bundle root (raw logs stay gitignored).
cat > "$OUT/run-summary.json" <<JSON
{
  "runId": "$RUN_ID",
  "timestampUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "sourceHead": "$(git -C "$REPO_ROOT" rev-parse HEAD)",
  "sourceTree": "$(git -C "$REPO_ROOT" rev-parse HEAD^{tree})",
  "sourceStatus": "$(git -C "$REPO_ROOT" status --porcelain | tr '\n' ';')",
  "driverImage": "$IMAGE_DRIVER",
  "driverImageId": "$(docker inspect -f '{{.Id}}' "$IMAGE_DRIVER")",
  "matchingImage": "$IMAGE_MATCHING",
  "matchingImageId": "$(docker inspect -f '{{.Id}}' "$IMAGE_MATCHING")",
  "rel01": {
    "tripId": "$REL01_TRIP",
    "driverId": "$REL01_D1",
    "nextDriverId": "$REL01_D2",
    "reservationId": "$REL01_RESERVATION",
    "driverVersion": $REL01_DRIVER_VERSION,
    "httpStatusAfterTimeout": "$REL01_HTTP",
    "result": "PASS"
  },
  "rel02": {
    "tripId": "$REL02_TRIP",
    "driverId": "$REL02_D3",
    "nextDriverId": "$REL02_D4",
    "reservationId": "$REL02_RESERVATION",
    "driverVersion": $REL02_DRIVER_VERSION,
    "killedContainerId": "$REL02_A_ID",
    "killedPid": $REL02_A_PID,
    "killedExitCode": $REL02_A_EXIT,
    "restartedContainerId": "$REL02_B_ID",
    "restartedPid": $REL02_B_PID,
    "result": "PASS"
  }
}
JSON

log "PASS: REL-01 and REL-02"
log "Evidence: $OUT"
