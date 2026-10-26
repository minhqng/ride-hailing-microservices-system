#!/usr/bin/env bash
# Helpers for the W09/W10 G2/G3 evidence campaign.
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=${REPO_ROOT:-$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)}
cd "$REPO_ROOT"
COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME:-rh-w09w10-g3}
export TRIP_DB='Host=127.0.0.1;Port=5432;Database=trip_db;Username=trip_app;Password=trip_dev_pw'
export MATCHING_DB='Host=127.0.0.1;Port=5432;Database=matching_db;Username=matching_app;Password=matching_dev_pw'
export DRIVER_BASE_URL=http://127.0.0.1:8102

harness() {
  docker run --rm --network host -v "$PWD":/src -w /src/scripts/test/w10-g3-harness \
    -e TRIP_DB -e MATCHING_DB -e DRIVER_BASE_URL \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet bin/Release/net10.0/RideHailing.W10Harness.dll "$@"
}

psqlq() { # db user sql
  docker exec "${COMPOSE_PROJECT_NAME}-postgres-1" psql -U "$2" -d "$1" -v ON_ERROR_STOP=1 -X -A -t -c "$3"
}

psqlj() { # db user sql -> json rows
  docker exec "${COMPOSE_PROJECT_NAME}-postgres-1" psql -U "$2" -d "$1" -v ON_ERROR_STOP=1 -X -A -t -c \
    "select coalesce(json_agg(t), '[]'::json) from ($3) t"
}

mkdriver() { # user uuid lat lon session
  local d
  d=$(curl -s -X POST http://127.0.0.1:8102/drivers -H 'content-type: application/json' \
      -d "{\"userId\":\"$1\"}" | jq -r .driverId)
  curl -s -X POST "http://127.0.0.1:8102/drivers/$d/online" -H 'content-type: application/json' \
      -d '{"expectedVersion":1}' >/dev/null
  curl -s -X POST "http://127.0.0.1:8102/drivers/$d/location" -H 'content-type: application/json' \
      -d "{\"deviceSessionId\":\"$4\",\"sequenceNo\":1,\"capturedAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",\"latitude\":$2,\"longitude\":$3}" >/dev/null
  echo "$d"
}

create_trip() { # rider idem
  harness create "$1" "$2" | jq -r .tripId
}

compute_round() { # trip
  local trip=$1 out
  for _ in 1 2 3 4 5; do
    out=$(curl -s -X POST http://127.0.0.1:8104/internal/matching/rounds -H 'content-type: application/json' \
      -d "{\"tripId\":\"$trip\",\"matchingRound\":1,\"pickupLongitude\":106.7009,\"pickupLatitude\":10.7769}")
    if [ "$(echo "$out" | jq '.candidates | length')" -gt 0 ] 2>/dev/null; then echo "$out"; return 0; fi
    sleep 1
  done
  echo "$out"; return 1
}

reserve_trip() { # trip driver roundJson
  local res
  res=$(harness reserve "$1" 1 "$2")
  echo "$res" | jq -e '.ok == true' >/dev/null
  echo "$res" | jq -r .reservationId
}

wait_trip_state() { # trip state timeout
  for _ in $(seq 1 ${3:-40}); do
    local s
    s=$(psqlq trip_db trip_app "select state from trips where trip_id='$1'")
    [ "$s" = "$2" ] && return 0
    sleep 0.5
  done
  return 1
}

wait_round_state() { # trip state
  for _ in $(seq 1 40); do
    local s
    s=$(psqlq matching_db matching_app "select status from matching_rounds where trip_id='$1' and matching_round=1")
    [ "$s" = "$2" ] && return 0
    sleep 0.5
  done
  return 1
}

queue_depth() {
  curl -s -u ride_app:ride_dev_pw "http://127.0.0.1:15672/api/queues/%2F/$1" | jq -r '.messages'
}

report() { harness report "$1"; }
