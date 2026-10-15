# A-W07 cancellation implementation and evidence

Base: main fcdc8b7. Scope: Trip-owned cancellation and durable cleanup obligations.

- Cancel in MATCHING, DRIVER_RESERVED or DRIVER_ACCEPTED atomically persists terminal state and exact cleanup intents. Repeated authorized HTTP cancellation returns 200 without mutation.
- Late reservation records release(tripId, driverId, reservationId, round); it cannot reopen CANCELLED or overwrite the current assignment. Late authorization records a reconciliation obligation, not an invented Payment void command.
- Cleanup completion requires a matching conclusive authority result. UNKNOWN, publish confirm and consumer ACK are not cleanup success.
- Migration backfills cancellation obligations from existing cancelled rows. Cleanup shares aggregate optimistic concurrency; losing transactions roll back intents.
- Compatibility compensation flags remain hints; the durable CleanupIntents collection is the recovery source.

## Observed execution

Prior execution in this workspace: Trip unit suite 260 passed; Trip integration suite 106 passed, zero failed (w7-trip-integration-final.trx). PostgreSQL 18.6 via Testcontainers. These results precede the two additional cancel/reserve agreement cases added to TripDriverAgreementTests.

Subsequent Release compilation including those two additional cases succeeded with zero warnings/errors. The additional cases have NOT been executed. Raw TRX files remain local in ignored TestResults directories.

## Boundaries and handoff

The agreement fixture uses Driver domain Release plus repository persistence in controlled schedules. It does not demonstrate a deployed B-W07 release adapter or background recovery worker. Production dispatch/retry and Payment operation lineage remain dependent on the later workflow/Outbox/Saga work. Pending obligations survive a restart but require that worker to converge; full DSL-002 runtime liveness is not claimed.

CREATED and READY_TO_START cancellation remain policy-unresolved. Human B/C review and a deployed distributed crash walkthrough have not occurred.
