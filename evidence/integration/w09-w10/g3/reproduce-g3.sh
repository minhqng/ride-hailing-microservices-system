#!/usr/bin/env bash
# Clean W09/W10 G3 campaign on the real compose stack.
# Prerequisite: a freshly reset stack (down -v, then up -d) with all services
# healthy, and the controlled-actor harness built (see README.md).
# Usage: ./reproduce-g3.sh
#        OUT=/path/to/output ./reproduce-g3.sh
set -Eeuo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$SCRIPT_DIR/g3lib.sh"
OUT=${OUT:-$REPO_ROOT/evidence/integration/w09-w10/outputs/g3-repro-$(date -u +%Y%m%dT%H%M%SZ)}
mkdir -p "$OUT/assertions" "$OUT/logs"
: > "$OUT/campaign.log"
log() { echo "[$(date -u +%H:%M:%S)] $*" | tee -a "$OUT/campaign.log"; }
trap 'log "ERROR at line $LINENO: $BASH_COMMAND"' ERR

driver_state() { curl -s "http://127.0.0.1:8102/drivers/$1"; }
ensure_online() {
  local d=$1 st
  st=$(driver_state "$d")
  if [ "$(echo "$st" | jq -r .status)" = "OFFLINE" ]; then
    curl -s -X POST "http://127.0.0.1:8102/drivers/$d/online" -H 'content-type: application/json' \
      -d "{\"expectedVersion\":$(echo "$st" | jq -r .version)}" >/dev/null
    sleep 1
  fi
}
retire_driver() {
  local d=$1 st
  st=$(driver_state "$d")
  if [ "$(echo "$st" | jq -r .status)" = "AVAILABLE" ]; then
    curl -s -X POST "http://127.0.0.1:8102/drivers/$d/offline" -H 'content-type: application/json' \
      -d "{\"expectedVersion\":$(echo "$st" | jq -r .version)}" >/dev/null
  fi
}

wait_for() { # timeout_seconds predicate...
  local t=$1; shift
  local i
  for i in $(seq 1 $((t*2))); do
    if "$@"; then return 0; fi
    sleep 0.5
  done
  return 1
}

trip_state_is() { [ "$(psqlq trip_db trip_app "select state from trips where trip_id='$1'")" = "$2" ]; }
saga_step_is() { [ "$(psqlq trip_db trip_app "select current_step from ride_sagas where trip_id='$1'")" = "$2" ]; }
saga_status_is() { [ "$(psqlq trip_db trip_app "select status from ride_sagas where trip_id='$1'")" = "$2" ]; }
driver_status_is() { [ "$(driver_state "$1" | jq -r .status)" = "$2" ]; }
queue_at_least() { [ "$(queue_depth "$1")" -ge "$2" ]; }
outbox_published() { [ "$(psqlq trip_db trip_app "select status from outbox_messages where aggregate_id='$1' and message_type='$2'")" = "PUBLISHED" ]; }
saga_error_is() { [ "$(psqlq trip_db trip_app "select coalesce(last_error_code,'') from ride_sagas where trip_id='$1'")" = "$2" ]; }
receipt_exists() { [ "$(psqlq trip_db trip_app "select count(*) from trip_message_receipts where trip_id='$1' and message_type='$2'")" -ge 1 ]; }
payment_outbox_is() { [ "$(psqlq payment_db payment_app "select status from outbox_messages where aggregate_id='$1' and message_type='$2'")" = "PUBLISHED" ]; }
capture() { # out trip driver reservation
  "$SCRIPT_DIR/capture_scenario.sh" "$2" "$3" "$4" > "$1" 2>>"$OUT/campaign.log"
}

start_trip_scenario() { # rider driver label
  local rider=$1 driver=$2 label=$3 trip
  ensure_online "$driver"
  trip=$(create_trip "$rider" "g3-$label-$(date +%s)")
  if ! [[ "$trip" =~ ^[0-9a-fA-F-]{36}$ ]]; then
    log "ERROR: $label create failed: $trip"
    exit 1
  fi
  echo "$trip" > "$OUT/$label.trip"
  sleep 1
  compute_round "$trip" > "$OUT/$label.round.json"
  local got
  got=$(jq -r '.candidates[0].driverId' "$OUT/$label.round.json")
  if [ "$got" != "$driver" ] || [ "$(jq '.candidates | length' "$OUT/$label.round.json")" -ne 1 ]; then
    log "ERROR: $label candidate isolation failed: $(cat "$OUT/$label.round.json")"
    exit 1
  fi
  harness reserve "$trip" 1 "$driver" > "$OUT/$label.reserve.json"
  jq -e '.ok == true' "$OUT/$label.reserve.json" >/dev/null
  wait_for 30 trip_state_is "$trip" DRIVER_RESERVED
  log "$label trip=$trip driver=$driver reservation=$(jq -r .reservationId "$OUT/$label.reserve.json")"
}

log "Ready check"
for p in 8101 8102 8103 8104 8105 8080; do
  [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:$p/health/ready)" = "200" ] || { log "service $p not ready"; exit 1; }
done

log "Create 5 isolated drivers"
: > "$OUT/drivers.jsonl"
specs=(
  "10.7775 106.7015 1"
  "10.7790 106.7030 2"
  "10.7750 106.6990 3"
  "10.7800 106.7050 4"
  "10.7730 106.6970 5"
)
for spec in "${specs[@]}"; do
  set -- $spec
  d=$(mkdriver "33333333-3333-3333-3333-33333333330$3" "$1" "$2" "campaign-session-$3")
  echo "{\"index\":$3,\"driverId\":\"$d\"}" >> "$OUT/drivers.jsonl"
done
D1=$(jq -r 'select(.index==1).driverId' "$OUT/drivers.jsonl")
D2=$(jq -r 'select(.index==2).driverId' "$OUT/drivers.jsonl")
D3=$(jq -r 'select(.index==3).driverId' "$OUT/drivers.jsonl")
D4=$(jq -r 'select(.index==4).driverId' "$OUT/drivers.jsonl")
D5=$(jq -r 'select(.index==5).driverId' "$OUT/drivers.jsonl")
sleep 2

# ---------------- Happy path (G3)
for d in "$D2" "$D3" "$D4" "$D5"; do retire_driver "$d"; done
start_trip_scenario 44444444-4444-4444-4444-444444444401 "$D1" happy
TRIP=$(cat "$OUT/happy.trip"); D=$D1; RES=$(jq -r .reservationId "$OUT/happy.reserve.json")
harness accept "$TRIP" "$D" "$RES" | tee "$OUT/logs/happy.accept.json"
wait_for 30 trip_state_is "$TRIP" READY_TO_START
harness start "$TRIP" "$D" "$RES" | tee "$OUT/logs/happy.start.json"
wait_for 30 trip_state_is "$TRIP" IN_PROGRESS
wait_for 30 receipt_exists "$TRIP" DriverTripStartedV1
harness complete "$TRIP" "$D" "$RES" | tee "$OUT/logs/happy.complete.json"
wait_for 30 trip_state_is "$TRIP" COMPLETED
wait_for 30 receipt_exists "$TRIP" DriverTripCompletedV1
wait_for 60 saga_step_is "$TRIP" CAPTURED
capture "$OUT/assertions/happy.json" "$TRIP" "$D" "$RES"
jq -e '.trip.state=="COMPLETED" and .saga.status=="COMPLETED" and .saga.current_step=="CAPTURED" and .matching.round[0].status=="RESERVED" and ([.payment.operations[]|select(.state=="SUCCEEDED")]|length)==2 and .driver.status=="AVAILABLE" and ([.receipts[]|select(.message_type=="DriverTripStartedV1" and (.dispositions|test("Applied|BusinessReplay")))]|length)==1' "$OUT/assertions/happy.json" >/dev/null
log "happy PASS"
retire_driver "$D1"

# ---------------- REL-09 terminal authorization failure
for d in "$D1" "$D3" "$D4" "$D5"; do retire_driver "$d"; done
start_trip_scenario 44444444-4444-4444-4444-444444444402 "$D2" rel09
TRIP=$(cat "$OUT/rel09.trip"); D=$D2; RES=$(jq -r .reservationId "$OUT/rel09.reserve.json")
KEY="$TRIP:authorize:v1"
curl -s -X POST http://127.0.0.1:8105/simulator/operations -H 'content-type: application/json' \
  -d "{\"operationKey\":\"$KEY\",\"tripId\":\"$TRIP\",\"type\":\"Authorize\",\"amountMinor\":56602,\"currency\":\"VND\",\"mode\":\"Decline\"}" > "$OUT/logs/rel09.preseed.json"
jq -e '.state=="Declined"' "$OUT/logs/rel09.preseed.json" >/dev/null
harness accept "$TRIP" "$D" "$RES" | tee "$OUT/logs/rel09.accept.json"
wait_for 60 saga_status_is "$TRIP" COMPENSATED
wait_for 30 trip_state_is "$TRIP" CANCELLED
capture "$OUT/assertions/rel09.json" "$TRIP" "$D" "$RES"
jq -e '.trip.state=="CANCELLED" and .saga.status=="COMPENSATED" and .saga.last_error_code=="DECLINED" and ([.outbox[]|select(.message_type=="ReleaseDriverReservationV1" and .count==1)]|length)==1 and ([.outbox[]|select(.message_type=="CapturePaymentV1" or .message_type=="VoidPaymentAuthorizationV1")]|length)==0 and .driver.status=="AVAILABLE"' "$OUT/assertions/rel09.json" >/dev/null
log "rel09 PASS"
retire_driver "$D2"

# ---------------- REL-10 capture failure after completion
for d in "$D1" "$D2" "$D4" "$D5"; do retire_driver "$d"; done
start_trip_scenario 44444444-4444-4444-4444-444444444403 "$D3" rel10
TRIP=$(cat "$OUT/rel10.trip"); D=$D3; RES=$(jq -r .reservationId "$OUT/rel10.reserve.json")
harness accept "$TRIP" "$D" "$RES" | tee "$OUT/logs/rel10.accept.json"
wait_for 30 trip_state_is "$TRIP" READY_TO_START
harness start "$TRIP" "$D" "$RES" | tee "$OUT/logs/rel10.start.json"
wait_for 30 trip_state_is "$TRIP" IN_PROGRESS
KEY="$TRIP:capture:v1"
curl -s -X POST http://127.0.0.1:8105/simulator/operations -H 'content-type: application/json' \
  -d "{\"operationKey\":\"$KEY\",\"tripId\":\"$TRIP\",\"type\":\"Capture\",\"amountMinor\":56602,\"currency\":\"VND\",\"mode\":\"TimeoutBeforeEffect\"}" > "$OUT/logs/rel10.preseed.json"
jq -e '.state=="Unknown"' "$OUT/logs/rel10.preseed.json" >/dev/null
harness complete "$TRIP" "$D" "$RES" | tee "$OUT/logs/rel10.complete.json"
wait_for 30 trip_state_is "$TRIP" COMPLETED
wait_for 60 saga_step_is "$TRIP" CAPTURE_PENDING
capture "$OUT/assertions/rel10.json" "$TRIP" "$D" "$RES"
jq -e '.trip.state=="COMPLETED" and .saga.current_step=="CAPTURE_PENDING" and ([.outbox[]|select(.message_type=="CapturePaymentV1" and .count==1)]|length)==1 and ([.payment.operations[]|select(.operation_type=="CAPTURE" and .state=="UNKNOWN" and .provider_effect_occurred==false)]|length)==1 and .driver.status=="AVAILABLE"' "$OUT/assertions/rel10.json" >/dev/null
log "rel10 PASS"
retire_driver "$D3"

# ---------------- REL-08 payment timeout after effect
for d in "$D1" "$D2" "$D3" "$D5"; do retire_driver "$d"; done
start_trip_scenario 44444444-4444-4444-4444-444444444404 "$D4" rel08
TRIP=$(cat "$OUT/rel08.trip"); D=$D4; RES=$(jq -r .reservationId "$OUT/rel08.reserve.json")
KEY="$TRIP:authorize:v1"
curl -s -X POST http://127.0.0.1:8105/simulator/operations -H 'content-type: application/json' \
  -d "{\"operationKey\":\"$KEY\",\"tripId\":\"$TRIP\",\"type\":\"Authorize\",\"amountMinor\":56602,\"currency\":\"VND\",\"mode\":\"TimeoutAfterEffect\"}" > "$OUT/logs/rel08.preseed.json"
jq -e '.state=="Unknown"' "$OUT/logs/rel08.preseed.json" >/dev/null
EFFECT_BEFORE=$(psqlq payment_db payment_app "select count(*) from provider_effects where operation_key='$KEY'")
[ "$EFFECT_BEFORE" = "1" ] || { log "REL-08 expected one pre-reconcile effect, got $EFFECT_BEFORE"; exit 1; }
harness accept "$TRIP" "$D" "$RES" | tee "$OUT/logs/rel08.accept.json"
wait_for 60 saga_error_is "$TRIP" PROVIDER_RESPONSE_LOST
capture "$OUT/assertions/rel08-before-reconcile.json" "$TRIP" "$D" "$RES"
OPID=$(jq -r .id "$OUT/logs/rel08.preseed.json")
curl -s -X POST "http://127.0.0.1:8105/simulator/operations/$OPID/reconcile" > "$OUT/logs/rel08.reconcile.json"
jq -e '.state=="Succeeded" and .providerEffectOccurred==true' "$OUT/logs/rel08.reconcile.json" >/dev/null
EFFECT_AFTER=$(psqlq payment_db payment_app "select count(*) from provider_effects where operation_key='$KEY'")
[ "$EFFECT_AFTER" = "1" ] || { log "REL-08 second effect detected"; exit 1; }
capture "$OUT/assertions/rel08-after-reconcile.json" "$TRIP" "$D" "$RES"
jq -e '.saga.current_step=="AUTHORIZE_PENDING" and ([.payment.operations[]|select(.operation_type=="AUTHORIZE" and .state=="SUCCEEDED" and .attempt_count==1)]|length)==1 and ([.payment.providerEffects[]]|length)==1' "$OUT/assertions/rel08-after-reconcile.json" >/dev/null
log "rel08 PASS"

# ---------------- REL-07 saga restart while Payment pending
for d in "$D1" "$D2" "$D3" "$D4"; do retire_driver "$d"; done
start_trip_scenario 44444444-4444-4444-4444-444444444405 "$D5" rel07
TRIP=$(cat "$OUT/rel07.trip"); D=$D5; RES=$(jq -r .reservationId "$OUT/rel07.reserve.json")
COMPOSE=(docker compose -p "$COMPOSE_PROJECT_NAME" -f "$REPO_ROOT/compose.yaml")
if [ -n "${COMPOSE_OVERRIDE:-}" ]; then COMPOSE+=(-f "$COMPOSE_OVERRIDE"); fi
"${COMPOSE[@]}" stop payment >/dev/null
harness accept "$TRIP" "$D" "$RES" | tee "$OUT/logs/rel07.accept.json"
SAGA_BEFORE=$(psqlq trip_db trip_app "select saga_id from ride_sagas where trip_id='$TRIP'")
[ -n "$SAGA_BEFORE" ] || { log "REL-07 saga was not created by accept"; exit 1; }
wait_for 30 outbox_published "$TRIP" AuthorizePaymentV1
wait_for 30 queue_at_least payment.commands.q 1
"${COMPOSE[@]}" stop trip >/dev/null
"${COMPOSE[@]}" start payment >/dev/null
wait_for 60 queue_at_least trip.payment-outcome.q 1
wait_for 30 payment_outbox_is "$TRIP" PaymentAuthorizedV1
capture "$OUT/assertions/rel07-before-trip-restart.json" "$TRIP" "$D" "$RES" || true
"${COMPOSE[@]}" start trip >/dev/null
wait_for 60 trip_state_is "$TRIP" READY_TO_START
wait_for 30 saga_step_is "$TRIP" AUTHORIZED
SAGA_AFTER=$(psqlq trip_db trip_app "select saga_id from ride_sagas where trip_id='$TRIP'")
[ "$SAGA_BEFORE" = "$SAGA_AFTER" ] || { log "REL-07 saga identity changed"; exit 1; }
capture "$OUT/assertions/rel07.json" "$TRIP" "$D" "$RES"
jq -e '.trip.state=="READY_TO_START" and .saga.status=="ACTIVE" and .saga.current_step=="AUTHORIZED" and ([.payment.operations[]|select(.operation_type=="AUTHORIZE" and .state=="SUCCEEDED")]|length)==1' "$OUT/assertions/rel07.json" >/dev/null
log "rel07 PASS saga=$SAGA_AFTER"

log "DONE"
