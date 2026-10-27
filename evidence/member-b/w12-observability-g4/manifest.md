# B-W12 Phase 1 evidence manifest

## 1. Source binding

| Item | Value |
|---|---|
| Repository | `minhqng/ride-hailing-microservices-system` |
| Branch | `feature/B-W12-observability-g4` |
| Base | W11 HEAD `3ba3724e7e7df378e8c234af685c027ad9d26863` |
| W11 PR | #44, open, base `main` `86062af028d01b86f07b1c902f040a41c41fe57f` |
| Implementation commit | `699a80151984ce91e3990be5621526df8bdcca56` |
| Implementation tree | `8f80eb728cd32aff0b3b67b39354759805e231d4` |
| Evidence commit | `3f2ed52` (bundle) + final metadata commit on this PR head |
| Evidence tree | `bbbbad0b650fb3f7a549e7ca6e390aab30d99588` (bundle tree) |
| Tested source | `699a80151984ce91e3990be5621526df8bdcca56` (evidence files do not affect code) |
| Source status at final run | clean code tree; evidence added after the run |

## 2. Environment

| Component | Version / identity |
|---|---|
| Host | Windows 10.0.26200 x64, Git Bash |
| Docker Engine | `29.8.2` (Docker Desktop, WSL2) |
| .NET SDK / runtime | `10.0.403` / `10.0.14` |
| PostgreSQL | `postgres:18.6` |
| RabbitMQ | `rabbitmq:4.3.6-management` |
| Redis | `redis:8.10.2` |
| Testcontainers | 4.15.0 |

Fixtures use real disposable PostgreSQL/RabbitMQ/Redis containers. No external
OTel collector is required for correctness; traces are asserted with an
in-memory `ActivityListener`.

## 3. Exact commands

```bash
cd D:/rh-w12b
export PATH="$HOME/.dotnet:$PATH"

dotnet build RideHailing.Distributed.slnx -c Release
dotnet test RideHailing.Distributed.slnx -c Release --no-build \
  --logger "console;verbosity=minimal"

# W12 focused proofs
dotnet test tests/Unit/RideHailing.DriverService.UnitTests/... -c Release \
  --filter "FullyQualifiedName~DriverMetricSemanticsTests"
dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests/... -c Release \
  --filter "FullyQualifiedName~DriverMetricsTests"
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests/... -c Release \
  --filter "FullyQualifiedName~MatchingMetricsTests|FullyQualifiedName~ProjectionMetricsTests|FullyQualifiedName~ReserveTraceContinuityTests|FullyQualifiedName~DriverAuthorizationExposureTests|FullyQualifiedName~DriverLocationIngressTests"
```

## 4. Test results

| Suite | Result |
|---|---|
| Driver unit | 131/131 |
| Matching unit | 104/104 |
| Driver integration | 113/113 |
| Matching integration | 158/158 |
| Platform integration (Outbox/receipts) | 60/60 |
| Trip integration (W9/W10) | 162/162 |
| Payment unit / integration | 28/28, 28/28 |
| Trip unit | 299/299 |
| Identity integration | 1 skipped (`RIDE_AUTH_FIXTURE` absent) |
| Full solution | 1083 passed, 1 skipped, 0 failed |
| W12 focused | 19/19 |

W12 focused composition: 2 Driver unit predicate tests, 3 Driver metrics
integration tests, 4 Matching metrics, 4 projection metrics, 3 trace,
2 authorization exposure, 1 location ingress.

## 5. Controlled boundaries

| Proof | Boundary / oracle |
|---|---|
| Driver reserve success | Real Driver PostgreSQL transaction + Outbox; metric after COMMIT; idempotent replay excluded; DB failure excluded |
| Driver conflicts / stale release | Real row-lock fencing; bounded reason labels; broker provenance refusal excluded from stale count |
| Matching attempt/unknown | Real matching_db ledger; one UNKNOWN transition per attempt; repeated execution on UNKNOWN does not re-count |
| Matching NO_DRIVER | Real terminalizer transaction + Outbox; replay of terminal round does not mint a second terminal metric |
| Reconcile | Stub transport at `IDriverClient`; real ledger; three real polls counted, then same-R resolution |
| Redis stale/latency | Real Redis 8.10.2 + production Lua function + real handler; counters at FCALL reply; unavailable = Degraded/Retry |
| Trace | Real Kestrel HTTP, real Driver transaction, in-memory ActivityListener; REL-01 barrier after commit |
| Authorization | Real Kestrel HTTP, real Driver DB/Outbox; exposure recorded as open finding |
| Location | Real Kestrel, real Rabbit publication, real Redis projection; 18 invalid requests, no mutation/publication |

## 6. Limitations / non-claims

- No authenticated principal contract exists; SEC-AUTH-01..04 are open
  (`SYSTEM_AUTH_PENDING`). No auth PASS is claimed.
- Reconciliation trace linkage requires a caller-supplied workflow/consumer
  span; traceparent is not persisted in the Matching ledger.
- `matching.round.candidate_count` measures the offered candidate sequence,
  normally the frozen snapshot; it is not re-read from the ledger.
- `matching_unknown_total` counts committed UNKNOWN attempt transitions, not
  pre-effect version-read/fence failures that leave the attempt IN_FLIGHT.
- Projection exact replays are `stale` by the frozen W4 contract (equal
  version/non-advancing sample); this is documented, not treated as a
  regression metric.
- W12-07…10 and G4 acceptance are not claimed.

## 7. Artifact hashes

`hashes.txt` records SHA-256 for every deliverable in this bundle.

## 8. CI status

`NOT TRIGGERED`. `.github/workflows/ci.yml` limits `pull_request.branches`
to `main`, `MemC-*-Distributed`, `feature/A-W09-trip-outbox` and
`feature/B-W09-driver-outbox`; the W11 stacked base
`feature/B-W11-deterministic-faults` is not listed. The PR head therefore has
zero check runs. This is not green, and it is not claimed as a CI pass. The
workflow is a shared/leader-owned file and was not modified in W12.
