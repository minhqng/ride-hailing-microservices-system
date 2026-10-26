#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$SCRIPT_DIR/g3lib.sh"
TRIP=$1
DRIVER=$2
RES=$3
SAGA=$(psqlj trip_db trip_app "select saga_id::text,status,current_step,step_version,reservation_id::text,authorize_operation_key,capture_operation_key,void_operation_key,last_error_code,requires_review_reason from ride_sagas where trip_id='$TRIP'")
[ "$SAGA" = "[]" ] && SAGA=null || SAGA=$(echo "$SAGA" | jq '.[0]')
TRIPROW=$(psqlj trip_db trip_app "select trip_id::text,state,version,current_matching_round,assigned_driver_id::text,reservation_id::text from trips where trip_id='$TRIP'" | jq '.[0]')
OUTBOX=$(psqlj trip_db trip_app "select message_type,count(*)::int as count,min(status) as min_status,max(status) as max_status from outbox_messages where aggregate_id='$TRIP' group by message_type order by message_type")
RECEIPTS=$(psqlj trip_db trip_app "select message_type,count(*)::int as count,string_agg(distinct disposition, ',') as dispositions from trip_message_receipts where trip_id='$TRIP' group by message_type order by message_type")
CLEANUP=$(psqlj trip_db trip_app "select key,kind,driver_id::text,reservation_id::text,matching_round,completed_at from trip_cleanup_intents where trip_id='$TRIP' order by key")
ROUND=$(psqlj matching_db matching_app "select matching_round,status,candidate_count,selected_driver_id::text,selected_reservation_id::text from matching_rounds where trip_id='$TRIP'")
ATTEMPTS=$(psqlj matching_db matching_app "select ordinal,driver_id::text,reservation_id::text,state from matching_attempts where trip_id='$TRIP' order by ordinal")
PAYMENT=$(psqlj payment_db payment_app "select operation_type,operation_key,state,provider_effect_occurred,provider_effect_id,result_code,attempt_count,completed_at from payment_operations where trip_id='$TRIP' order by created_at")
EFFECTS=$(psqlj payment_db payment_app "select operation_key,effect_id,committed_at from provider_effects where operation_key like '$TRIP%' order by operation_key")
PAYOUTBOX=$(psqlj payment_db payment_app "select message_type,status from outbox_messages where aggregate_id='$TRIP' order by occurred_at")
DRIVERROW=$(psqlj driver_db driver_app "select driver_id::text,status,version,reserved_trip_id::text,reservation_id::text,reserved_round,active_trip_id::text from drivers where driver_id='$DRIVER'" | jq '.[0]')
jq -n \
  --arg capturedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg tripId "$TRIP" --arg driverId "$DRIVER" --arg reservationId "$RES" \
  --argjson trip "$TRIPROW" --argjson saga "$SAGA" \
  --argjson outbox "$OUTBOX" --argjson receipts "$RECEIPTS" --argjson cleanup "$CLEANUP" \
  --argjson round "$ROUND" --argjson attempts "$ATTEMPTS" \
  --argjson payment "$PAYMENT" --argjson effects "$EFFECTS" --argjson paymentOutbox "$PAYOUTBOX" \
  --argjson driver "$DRIVERROW" \
  '{capturedAt:$capturedAt,tripId:$tripId,driverId:$driverId,reservationId:$reservationId,trip:$trip,saga:$saga,outbox:$outbox,receipts:$receipts,cleanup:$cleanup,matching:{round:$round,attempts:$attempts},payment:{operations:$payment,providerEffects:$effects,outbox:$paymentOutbox},driver:$driver}'
