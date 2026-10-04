# Member C — Week 4 contracts and topology

## Message envelope

`MessageEnvelope<T>` is the shared mechanics wrapper for an integration payload. It carries a stable caller-supplied `messageId`, type/version, occurrence time, workflow correlation, optional causation, aggregate identity/version, sagaId, operationKey and typed payload. W3C trace context is carried separately in RabbitMQ headers (Doc 02 section 5.4). `messageId` must be persisted by an Outbox before publication; creating a new envelope during relay would break deduplication.

Incompatible semantic changes use a new versioned message type (for example `PaymentAuthorizedV1` → `PaymentAuthorizedV2`). Trace fields are diagnostic metadata and are not required for business recovery.

## W3C trace propagation

`TraceHeaderPropagation` copies `traceparent`/`tracestate` between Activity context and RabbitMQ headers. It does not create correlation or idempotency keys. Missing or malformed tracing metadata does not affect processing correctness.

## Topology declared by code

Payment Service declares durable topic exchanges `ride.commands` and `ride.events`, then the following consumer queues with quorum type and bindings:

| Queue | Exchange | Routing keys (Doc 02 section 6) |
|---|---|---|
| matching.ride-requested.q | ride.events | trip.ride-requested.v1 |
| trip.driver-state.q | ride.events | driver.reserved/reservation-released/trip-started/trip-completed.v1 |
| trip.matching-outcome.q | ride.events | matching.failed.v1 |
| trip.payment-outcome.q | ride.events | payment.authorized/authorization-failed/authorization-voided/captured/capture-pending/requires-review.v1 |
| matching.driver-projection.q | ride.events | driver.available/offline/reserved/reservation-released/trip-started/trip-completed/location-accepted.v1 |
| payment.commands.q | ride.commands | payment.authorize/capture/void-authorization.v1 |
| driver.commands.q | ride.commands | driver.release-reservation/mark-trip-started/mark-trip-completed.v1 |
| matching.commands.q | ride.commands | matching.cancel-round.v1 |
| notification.business-events.q | ride.events | trip.#, driver.#, payment.#, matching.# |

Slash-separated entries above abbreviate separate routing keys; executable `RideTopology` declares each key explicitly. Queue names are consumer capabilities, not routing keys. `MessageProperties.Create` sets persistent delivery and stable envelope identities; confirms/ACK remain W8.

Repeated declarations preserve the same broker semantics. No consumers, confirms, retry/DLQ flow or Payment event publishing are enabled in W4; those are later playbook weeks.

## Local config rule

`.env.example` contains local development placeholders. Keep real credentials in the ignored `.env`, and never place secrets, payment data or passenger PII in trace headers. Local one-node quorum declarations are not evidence of high availability.

## Verification and review status

C-W04-01..06: the envelope supports all Doc 02 section 5.2 identities, V1 schema validation, trace header helpers and persistent message properties; topology covers the Doc 02 section 6 catalog with independent fan-out queues.

C-W04-07: `scripts/test/topology-smoke.py` checks exact broker declarations, restarts Payment to redeclare, then routes every catalog key to expected queues using a disposable broker. C-W04-08: simulator controls are verified absent in Production. The script uses local placeholder Rabbit credentials by default; export RABBITMQ_USER/PASSWORD when overriding them. C-W04-09: W2 platform smoke verifies DB isolation. C-W04-10: actual A/B contract review remains pending.

In-process `MessageContractTests` prove wire roundtrip, recovery identities, malformed versions/IDs, byte/string/memory trace propagation, stale-header removal and persistence properties. They are not broker integration evidence; CI runs the separate real-broker smoke.

Auth negative test skeleton is implemented in `AuthNegativeTests.cs`: seven real HTTP cases with an authenticated positive control and authoritative DB snapshots. Set `RIDE_AUTH_FIXTURE` after A/B handoff; without fixtures the tests explicitly skip. See `docs/member-c-auth-handoff.md` for configuration and acceptance. W4 does not claim production auth is complete. No CI result substitutes for A/B review.
