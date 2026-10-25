# A-W12-02 — Trip/Saga metric instruments (emitted signals, verified semantics)

Meter `RideHailing.Trip` (registered on the shared OpenTelemetry pipeline in
`Program.cs`; one registry, no second exporter). Dot names below; an OTel
Prometheus exporter renders the Doc 05A §20.2 underscore forms.

| Instrument | Kind | Dimensions | Authority source | Verified semantics (from `TripW12TelemetryTests` + `TripW12TracePropagationTests`) |
| --- | --- | --- | --- | --- |
| `trip.created` → `trip_created_total` | Counter | — | CreateRideHandler's committed SaveChanges | +1 per COMMITTED new Trip only; idempotent replay and create-race reconciliation never count |
| `trip.cancelled` → `trip_cancelled_total` | Counter | — | CancelRideHandler's committed SaveChanges | +1 per committed new CANCELLED transition; terminal replay never counts |
| `trip.transition.rejected` → `trip_transition_rejected_total` | Counter | `reason` ∈ {active_trip_conflict, idempotency_conflict, invalid_transition, terminal_state, cancellation_policy_unresolved, stale_identity, status_conflict, identity_required, authority_rejected} | domain decision boundaries (HTTP endpoints + consumer dispositions) | Domain rejections only. OCC conflicts / broker outage are not counted; input-validation 400s are not business rejections; NeedsReview facts counted once — receipt redelivery does not re-count |
| `saga.compensation` → `saga_compensation_total` | Counter | — | committed saga status transition into COMPENSATING (snapshot compare around the transaction) | +1 per committed NEW entry, via HTTP cancel and via consumer paths; replay never counts |
| `saga.requires_review` → `saga_requires_review_total` | Counter | — | committed saga status transition into REQUIRES_REVIEW | same entry-once semantics |
| `saga.active{step}` → `saga_active{step}` | ObservableGauge | `step` ∈ canonical RideSagaStep names (6) | `TripSagaBacklog.QueryAsync`: indexed GROUP BY over durable `ride_sagas` (non-terminal statuses), refreshed by `TripMetricsObserver` (default 15 s, `TripMetrics:RefreshSeconds` 5–300) | Durable-state backed: survives restart by re-derivation; test asserts exported values equal the SQL aggregate per step |
| `outbox.pending` / `outbox.oldest_pending.age` / `outbox.failed_visible` | ObservableGauges (C-owned, shared meter `RideHailing.Messaging`) | — | `OutboxRelay.ObserveBacklogAsync` after every relay cycle, from durable `outbox_messages` rows | REUSED unchanged; Trip adds no duplicate measurement. Propagation test asserts the gauge reflects 0 pending after the real relay cycle published the durable row |

High-cardinality ids (tripId, reservationId, sagaId, operationKey,
messageId, riderId, traceId) are span/log diagnostic fields only — never
metric labels. `TripW12TelemetryTests.EmittedSignals_UseBoundedVocabularyAndSafeFieldsOnly`
asserts the allowed tag-key set {reason, step}, the bounded value
vocabularies, and that no span tag key carries rider/token/secret/payload/
location names.

Telemetry failure isolation: counter/gauge/span emission happens after
commit (or on Activity objects that may be null without a listener); no
instrument writes the database inside a business transaction, and
`TripMetricsObserver` failures keep the last durable snapshot and retry on
the next interval without affecting any business outcome.
