# B-W12-04 trace proof matrix

Test class: `tests/Integration/RideHailing.MatchingService.IntegrationTests/ReserveTraceContinuityTests.cs`.
The tests use an in-memory `ActivityListener` only; there is no external
collector dependency. Driver endpoints, repositories, Outbox transaction and
Matching orchestrator are production implementations over a real Kestrel socket.
The Driver test host calls the C-owned `AddRideTelemetry` plus the B-owned
`AddDriverTelemetry`, so the bootstrap registration is exercised, not only
inspected. B-owned `AddMatchingTelemetry` registers the Matching source, meter
and HTTP client instrumentation on the same OTel pipeline; C's
`PlatformBootstrap` was not modified.

Source registration: `DriverTelemetry.SourceName` (`RideHailing.DriverService`),
`MatchingTelemetry.SourceName` (`RideHailing.MatchingService`), the existing
`RideHailing.Messaging`, ASP.NET Core instrumentation from C's bootstrap, and
HTTP client instrumentation from the B-owned Matching extension.

## 1. Normal successful Reserve

Test: `SuccessfulReserve_Produces_One_Parent_Chain_To_The_Driver_Transaction`.

| Depth | Span | Asserted relationship |
|---|---|---|
| 0 | `w12.workflow` (test Source `W12.Test.Workflow`) | trace origin |
| 1 | `matching.round.execute` (Source `RideHailing.MatchingService`) | captured parent = workflow; tag `matching.round.outcome=Reserved` |
| 2 | `matching.attempt.reserve` (Matching) | parent = round; tag `matching.attempt.outcome=Reserved` |
| 3 | `System.Net.Http` `POST` | parent = attempt; HTTP request carries W3C trace context |
| 4 | ASP.NET Core server `HTTP POST` (Driver) | parent = HTTP client span; same `TraceId` across the socket |
| 5 | `driver.reserve` (Source `RideHailing.DriverService`) | parent = Driver server span; tag `driver.reserve.outcome=won`; `reservation.id` equals the returned reservation |

All five spans share one `TraceId`. The same test also sends a location request
inside the workflow so the privacy assertion covers a payload that actually
contains raw coordinates and a device session.

## 2. W11 REL-01: response lost after Driver commit

Test: `LostResponse_Classifies_Unknown_Without_Fabricating_A_Success_Then_Resolves_SameReservation`.

| Phase | Span | Asserted classification |
|---|---|---|
| Reserve | `matching.round.execute` | `outcome=BlockedOnUnknown` |
| Reserve | `matching.attempt.reserve` | `outcome=Unknown`, `matching.unknown.reason=RESERVE_TRANSPORT` |
| Reserve | `System.Net.Http POST` | `Status=Error`; **no** `http.response.status_code=200` tag |
| Reserve | `driver.reserve` | `driver.reserve.outcome=won` — the Driver transaction really committed before the response was lost |
| Reconcile | `w12.workflow.reconcile` (captured test workflow span) | test starts it with the round's `TraceId/SpanId` as parent; this is a test-supplied context, not a persisted production traceparent |
| Reconcile | `matching.attempt.reconcile` | parent = captured reconcile workflow span; `matching.reconcile.outcome=Reserved`; `reservation.id` equals the same reservation |
| Reconcile | `System.Net.Http GET` + Driver server span | same trace; authoritative truth read for the SAME reservation identity |

The missing response therefore cannot fabricate a successful caller-side
response span: the client span is Error, the attempt is UNKNOWN, and the
durable reservation is only resolved by reconciliation with the same
`reservationId`. Cross-restart/cross-worker trace linkage is an explicit W12
limitation: the ledger stores no W3C context, so a production reconciler must
carry a workflow/consumer span. W12 does not claim otherwise.

## 3. Rejected Reserve and stale Release

Test: `Rejected_Reserve_And_Stale_Release_Are_Classified_On_Their_Own_Spans`.

| Operation | Span | Classification |
|---|---|---|
| First Reserve | `driver.reserve` | `won` (HTTP 200) |
| Foreign-reservation Reserve | `driver.reserve` | `conflict` (HTTP 409) |
| Stale Release (wrong reservation token) | `driver.release` | `conflict` (HTTP 409) |

The two reserve spans are ordered `won`, `conflict` in the same trace; the
release conflict has its own span. No rejected operation is labeled as a
success.

## 4. Privacy assertion

For every span in each trace, including the location-request trace, the tests
assert:

- no tag key containing `authorization`, `password`, `token`, `sessionId`,
  `latitude` or `longitude`;
- no tag value containing `Bearer`, the raw longitude literals `106.6`/`106.7`
  or the test device session `w12-trace-location`;
- tags are limited to business identity/provenance (`trip.id`,
  `reservation.id`, `driver.id`, `matching.round`, attempt ordinal), outcome,
  HTTP timing/status metadata, and standard runtime tags.

No raw location, credentials, tokens or payload bodies are emitted into spans.
Metric labels are separate and contain none of these identities (see
`../metrics/semantics.md`).

## 5. Known limitation

Reconciliation is trace-linked only when its caller supplies a workflow or
consumer span. W12 does not persist `traceparent` in `matching_rounds` /
`matching_attempts`, so a reconciler that starts with no ambient activity
starts a new trace. The test demonstrates the supported in-process pattern and
does not claim durable cross-restart linkage.
