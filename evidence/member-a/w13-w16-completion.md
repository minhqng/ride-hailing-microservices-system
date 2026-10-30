# Member A W13–W16 completion ledger

Date: 2026-10-30

## Completed in repository

- W13: durable Saga/backlog metrics, Trip correctness counters, Grafana Trip/Saga dashboard, mock-data walkthrough, and invariant-auditor support for post-load checks.
- W14: Trip-side backlog/recovery observability, confirmed-Outbox semantics, restart-safe Saga gauge and an explicit application-level HA claim boundary.
- W15: feature-surface freeze, migration chain through W10, FSM/Outbox/Saga report, crash-window and compensation diagrams, evidence manifest links.
- W16: deterministic release gate, mock demo script, live compose/readiness verification and backup fault/restart evidence.
- Integration repair: Trip Payment commands now carry the same required `operationKey` in both envelope and payload, matching the current Payment consumer contract.

## Executed local evidence

| Check | Result |
| --- | --- |
| `dotnet build RideHailing.Distributed.slnx -c Release` | PASS, 0 errors; the stacked A branch reports one pre-existing B-owned xUnit analyzer warning |
| Trip unit suite | PASS 299/299 |
| `TripW09|TripW10` real infrastructure | PASS 31/31 |
| `TripW11FaultPointTests` | PASS 11/11 |
| `TripW11FaultProcessTests` | PASS 5/5 |
| `TripW12*` | PASS 19/19 |
| Compose core + obs readiness | PASS (Gateway, Identity, Driver, Trip, Matching, Payment, Grafana, Prometheus and Jaeger HTTP 200) |
| Trip migration head | `20261025062925_W10_RideSaga` |
| `trip.payment-outcome.q` consumer count | 1 |
| Prometheus `saga_active_sagas` mock snapshot | AUTHORIZED=1, AUTHORIZE_PENDING=1, CAPTURE_PENDING=2, RELEASE_PENDING=1 |

Canonical earlier bundles remain under `evidence/member-a/w09-outbox`, `w10-saga`, `w11-fault-proofs` and `w12-core-gate`.

## Not self-claimed

- Independent B/C peer reruns and oral rehearsal counts.
- A release tag or remote CI result for this dirty combined working tree.
- Production authenticated end-to-end client flow while Identity remains a skeleton.
- Whole-system HA; only application behavior under the tested failure scenario is claimed.
