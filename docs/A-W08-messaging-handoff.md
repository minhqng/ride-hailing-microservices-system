# A-W08 Trip messaging: implementation and handoff

## Branch dependencies

Base: `MemC-W8-Distributed` (C's canonical messaging runtime), plus A-W7
cancellation commit `1dffb26` (original A-W7 commit `d5db76e`).
No B-W8 implementation was merged into this branch.

## Runtime

1. Apply Trip migrations through `W08_TripMessageReceipts` using the Trip-owned
   database credential. Startup does not migrate production databases.
2. Set `TripMessaging__Enabled=true`; supply `RabbitMq__Host`,
   `RabbitMq__Username`, `RabbitMq__Password`, optional port/vhost. In compose,
   set `TRIP_MESSAGING_ENABLED=true` before starting Trip.
3. Read `/health/ready`: requires an up-to-date owned database and all three
   consumer connections when messaging is enabled. Workers also wait for the
   owned schema before registering broker consumers. `/health/live` stays process-only.
4. Authenticated `POST /trips` persists creation, opens MATCHING and publishes
   `RideRequestedV1` on `ride.events / trip.ride-requested.v1`.
   Existing authentication requirements remain: production must receive a
   trusted ClaimsPrincipal from the shared authentication integration.
5. An unconfirmed publish returns 503 `MATCHING_PUBLICATION_PENDING`, including
   the saved tripId. Retry the **same Idempotency-Key and body**. Do not create
   another ride. A progressed or terminal trip is not published again.

The event uses the existing RideRequestedV1 shape, deterministic messageId
`trip:{tripId:N}:ride-requested:{round}`, correlationId = tripId, and persisted
matching timestamp/version. C propagates W3C trace headers through producer and
consumer spans. Publication confirm does not prove matching or reservation.

## Consumer contract

| Queue | Accepted messages | Local action |
|---|---|---|
| trip.driver-state.q | DriverReservedV1 | exact tripId/driverId/reservationId/matchingRound; aggregate guard or cancelled cleanup |
| trip.driver-state.q | DriverReservationReleasedV1 | complete only the matching cancelled-trip cleanup identity |
| trip.driver-state.q | DriverTripStartedV1 / DriverTripCompletedV1 | existing exact-assignment and lifecycle guards |
| trip.matching-outcome.q | MatchingFailedV1 | tripId + matchingRound, fenced domain transition |
| trip.payment-outcome.q | PaymentAuthorizedV1, PaymentAuthorizationFailedV1, PaymentAuthorizationVoidedV1, PaymentCapturedV1, PaymentCapturePendingV1, PaymentRequiresReviewV1 | durable PendingWorkflow envelope; late authorization on CANCELLED also records reconciliation |

Contracts beyond B's currently published shapes are A's intake requirements,
not a claim that B/C already publish them. Broker access controls must restrict
authority routes to the owning service; a messageType string is not authentication.

Dedup identity is (queue/consumer, messageId). The insert claim, Trip change and
receipt disposition commit together. Concurrent duplicate claims serialize in
PostgreSQL. Same ID with different canonical business content fails visibly.
Correlation/causation and publish timestamps are excluded from the semantic
fingerprint; retained original envelopes make legacy receipts compatible.
Supplied Driver aggregate identity, status and version must agree with the event.
A new ID
still passes all domain guards. Stale or contradictory facts retain their full
envelope with NeedsReview, without changing Trip. Payment envelopes remain
PendingWorkflow until Saga can prove operation lineage; tripId alone never
authorizes payment or advances READY_TO_START.

ACK follows durable completion (including an explicit review obligation).
Malformed input uses C's poison/error path. Known transient local DB failures
use C's bounded retries. Connection/transfer failure causes supervised disposal
and reconnect; unacknowledged deliveries can return. No remote business calls
occur inside the receipt transaction. Events for an unknown Trip are retained as
PendingWorkflow / TRIP_NOT_FOUND, with their full envelope, before ACK. They need
explicit reconciliation; replay of the same message does not silently apply them.
Late reservations for MATCH_FAILED also create exact cleanup and cannot reopen
the Trip; matching release evidence completes that obligation.

## Inspect pending work

Use the Trip-owned database; do not query Driver or Payment databases in production:

```sql
SELECT trip_id, consumer, message_id, message_type, correlation_id, disposition, reason, recorded_at
FROM trip_message_receipts
WHERE disposition IN ('NeedsReview', 'PendingWorkflow')
ORDER BY recorded_at;

SELECT trip_id, key, kind, driver_id, reservation_id, matching_round, created_at
FROM trip_cleanup_intents
WHERE completed_at IS NULL
ORDER BY created_at;
```

Original envelopes are retained in envelope_json for authorized reconciliation.
Do not delete receipts to retry an event, reuse a messageId with corrected content,
or mark cleanup complete because of a broker ACK. Retention/pruning must preserve
the broker replay horizon and unresolved obligations; no pruning job is included.

## B/C handoff and unresolved integration

- B-W8 DriverStateEventPayload currently has no matchingRound. New reservations
  without it are retained as NeedsReview / RESERVATION_ROUND_REQUIRED. B must
  supply authoritative persisted attempt context (or agree a versioned contract).
  A never guesses the current round.
- Release events must retain the released tripId/reservationId even after Driver
  becomes AVAILABLE. A driver-only snapshot cannot complete cleanup.
- Payment events are a W8 skeleton. Saga operationKey/authorization lineage and
  actual void/capture decisions are W10+; PendingWorkflow is not payment success.
- Cleanup dispatch/automatic convergence needs the later workflow/Outbox worker.
  W7 records durable obligations and exact completion guards, not a running saga.
- Human B/C review and the joint crash walkthrough remain review tasks.

## Crash-window walkthrough

| Window | Recovery / limitation |
|---|---|
| Create committed, before StartMatching | same HTTP key reloads saved trip and opens round |
| MATCHING committed, before publish | same HTTP key reconstructs stable event; **no autonomous recovery yet** |
| Publish reached broker, confirm/HTTP reply lost | explicit retry republishes same ID/content; downstream dedup required |
| Cancellation races publication | late Driver outcome cannot reopen CANCELLED; exact cleanup retained |
| Receipt claimed, effect not committed | transaction rollback removes claim and effect; redelivery can apply |
| Effect and receipt committed, before ACK | redelivery sees matching receipt; no second effect |
| New messageId for old reservation/round | domain identity/state guards still apply; review evidence retained |
| Error/retry transfer unconfirmed | C leaves delivery unacked; worker disposes and reconnects |

The W9 transactional Outbox closes the DB/publish gap. W8 does not claim
exactly-once delivery, autonomous publication recovery, or end-to-end Saga completion.

## Verification

See `TripW08MessagingTests`: actual HTTP host with opt-in worker, real PostgreSQL
and RabbitMQ, stable publication, manual ACK, trace continuity, dedup, stale
generation, cancellation, pending Payment, rollback and commit-before-ACK
connection-close redelivery. The Matching-style sink is explicitly a test-owned
durable handler; it is not B's deployed matching service or a full dispatch demo.

Execution counts and limitations are recorded in A-W08-verification.md after runs.
