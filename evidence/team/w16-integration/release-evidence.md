# W16 cross-owner release evidence

Source: `17ca928d5fdd9ef64bba16574503045317033b27` / tree
`59a740ec266acf219bfbbd9e75d98080b6926e06`.

This bundle records the automated technical release gate. It does not claim
human rehearsals, oral defense, peer sign-off or a published release.

## Contract reconciliation

| Contract | Implementation evidence | Runtime/evidence proof |
| --- | --- | --- |
| Trip command/outcome ↔ Payment authority | `TripSagaIntents` places the same stable `operationKey` in the message envelope and payload; `PaymentCommandHandlers` consumes `command.OperationKey`. | Integrated core-runtime scenarios authorize/capture/void; AUD-05 verifies one provider effect per operation key; payment smoke verifies same-key replay/conflict and timeout-after-effect reconciliation. |
| Saga ↔ Matching/Driver reservation authority | Saga stores exact reservation and operation lineage; Matching persists write-ahead attempts and reconciles the same reservation; Driver owns the reservation transaction. | Integrated completed/cancelled/declined scenarios; B real-provider three-trial contention and response-loss proof; AUD-01/04. |
| Driver lifecycle ↔ Trip guarded transitions | Driver lifecycle effects retain exact Trip/reservation ownership; Trip accepts only the current round and legal guarded transitions. | Core-runtime controlled production lifecycle actor; AUD-04; W11/W12 source-bound race/fencing tests. |
| Outbox/Inbox ↔ A/B local transactions | Each owning service commits state plus Outbox in one local transaction; relay confirms before `PUBLISHED`; consumers commit effect + dedup before ACK. | Pending-authorization check asserts no false `PUBLISHED`; W14 G5 recovery; W11/W12 duplicate/redelivery and crash-window evidence. |
| Identity/Gateway ↔ protected endpoints | Gateway routes public ride/auth paths; Identity issues short-lived RSA JWTs; internal service calls use service grants; domain fencing remains separate. | Security gate executes 55 real HTTP boundary checks; dedicated Identity lane passes 7/7 negative cases; core-runtime gate uses authenticated public intake. |

Envelope identity, correlation, traceparent, matching round, reservation ID,
expected version, terminal monotonicity, dedup receipts, publisher confirm,
ACK-after-commit and Outbox pending/published semantics are covered by the
integrated gates or the retained W11–W14 source-bound evidence. No W15/W16
runtime invariant changed.

## Journey coverage

| Required journey | Coverage | Where |
| --- | --- | --- |
| Happy ride | Integrated completed scenario across real providers | Exact-tree CI core-runtime gate |
| Cancellation and exact compensation | Integrated cancellation/void scenario | Exact-tree CI core-runtime gate |
| Payment decline | Integrated terminal-decline/release scenario | Exact-tree CI core-runtime gate |
| Payment capture recovery / response loss | Deterministic Payment suites and response-loss-after-restart smoke; W10/W11 accepted evidence | Payment smoke + source-bound bundles |
| UNKNOWN reservation/reconciliation | Three real-provider trials: post-commit response loss, UNKNOWN, no D2, same-R reconcile | B release evidence + rehearsal run |
| Crash/restart/redelivery | W14 G5 broker/backlog recovery and W11 process/restart proof; runtime source unchanged | W11/W14 source-bound evidence |

## G4/G5 preservation

W15/W16 add reports, evidence, CI/release configuration, demo tooling and one
Matching test assertion. The only `tests/` change is an additional
`AUTHORITY_PROOF` output in `LostReserveResponseHttpTests`; no production
source is changed. Consequently the accepted G4/G5 campaigns retain their
original source identities, while the exact-tree CI re-executes the integrated
G4-style authority journeys. A changed invariant is never certified by an old
run.

## Release and security results

- Release overlay renders with all eight operator secrets and fails closed
  without them (`must be set for the release overlay`).
- Secret scan passes on the exact candidate; CI records no credentials.
- Compose release/HA configuration renders; configuration validation is not
  runtime failover proof.
- Integration test delta: one additional assertion payload in a real
  Matching/Driver response-loss test; no product behavior change.

## Rehearsal accounting

- Automated technical rehearsals: B's harness runs three independent
  empty-volume trials with real PostgreSQL/Redis/RabbitMQ and shared
  complete/cancel/decline journeys; the integrated core-runtime gate runs the
  completed, cancellation/void and decline scenarios with AUD-01..08.
- Human team rehearsals: NOT PERFORMED. No spoken timing, role handoff,
  peer acknowledgement or oral defense is claimed.
- Final release tag: NOT CREATED.

## Limitations

- Human/course gates cannot be manufactured by automation.
- HA claims retain the recorded W14 scope, not whole-system or multi-region HA.
- Performance claims retain the measured single-host environment and sample
  limits.
- Mock dashboard/traffic tools are presentation aids, not correctness proof.
