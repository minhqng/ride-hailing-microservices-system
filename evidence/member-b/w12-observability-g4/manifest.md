# B-W12 Phase 2 evidence manifest

## 1. Source binding

| Item | Value |
|---|---|
| Repository | `minhqng/ride-hailing-microservices-system` |
| Branch | `feature/B-W12-observability-g4` |
| PR | #45, base `feature/B-W11-deterministic-faults` |
| Base | W11 head `3ba3724e7e7df378e8c234af685c027ad9d26863` (PR #44) |
| Tested B code head | `3f7aa18d26739085b4b8d82b06290233cf76a40f` |
| Tested tree | `208b4e74e4fca0d1d7dd4c883bff96c9a2a3fae1` |
| Audit tooling commit message | `test(audit): add B W12 authoritative G4 verification` |
| Evidence commit message | `docs(evidence): close B W12 regression and G4 handoff` (docs/evidence only) |
| Source status at formal start | clean |
| A-owned evidence head | `132ecfd4ebc7f452af5e28f030bf66754e9b0df7` (unmerged A-W12) |

Production code at the tested head is unchanged from W12 Phase 1
`699a80151984ce91e3990be5621526df8bdcca56`; Phase 2 adds verification tooling
and evidence only.

## 2. Environment

| Component | Version / identity |
|---|---|
| Host | Windows 10.0.26200 x64, Git Bash |
| .NET SDK / runtime | `10.0.403` / `10.0.14` |
| Docker Engine | `29.8.2` (Docker Desktop, WSL2) |
| PostgreSQL | `postgres:18.6` (Testcontainers, disposable) |
| RabbitMQ | `rabbitmq:4.3.6-management` |
| Redis | `redis:8.10.2` |
| Testcontainers | 4.15.0 |

Fixtures use real disposable infrastructure. The auditor additionally opens
read-only PostgreSQL transactions (`SET TRANSACTION READ ONLY`,
repeatable-read) for every verification query.

## 3. Exact commands

```bash
cd D:/rh-w12b
export PATH="$HOME/.dotnet:$PATH"

# Formal full regression
dotnet build RideHailing.Distributed.slnx -c Release
dotnet test RideHailing.Distributed.slnx -c Release --no-build \
  --logger "console;verbosity=normal"

# W11 five-pass critical campaign (clean tree)
./scripts/fault/b-w11-full-campaign.sh

# B authoritative auditor + non-vacuity controls
./scripts/audit/b-w12-g4.sh
```

## 4. Formal regression results

| Lane | Result |
|---|---|
| Full solution | 1101 passed, 1 skipped, 0 failed (1102 total) |
| Driver unit | 131/131 |
| Driver integration | 113/113 |
| Matching unit | 104/104 |
| Matching integration | 158/158 |
| Platform Outbox/dedup integration | 60/60 |
| Trip unit | 299/299 |
| Trip integration (W9/W10) | 162/162 (TripW09 17, TripW10 14) |
| Payment unit / integration | 28/28, 28/28 |
| B12 audit | 18/18 |
| System tests | 0 discovered — NOT RUN (not counted as PASS) |
| Identity integration | 1 skipped (`RIDE_AUTH_FIXTURE` absent) — not verification |
| W12 focused | 19/19 (2 Driver metric unit, 3 Driver metric integration, 4 Matching metrics, 4 projection metrics, 3 trace, 2 auth exposure, 1 location) |

Build: 0 errors, 14 warnings (C-owned Payment nullable warnings, pre-existing
Driver snapshot type-conflict warnings and xUnit analyzer style warnings); the
new B12 audit project contributed no warnings and there was no build or test
failure. Initial failures: none. Assertions weakened: none.

## 5. W11 critical regression (five-pass campaign)

- Command: `./scripts/fault/b-w11-full-campaign.sh` at the audit commit
  (production-identical to the final tested head) with a clean tree.
- Scenarios: REL-01, REL-02, CON-02, CON-04, REL-12, REL-13, REL-14.
- Result: five consecutive passes; see `test-output/w11-critical-regression.txt`.
- REL-01/02 run real Driver/Matching containers (response barrier after commit;
  SIGKILL + restart), not transport stubs.
- W12 instrumentation did not break the W11 fault proofs.
- The campaign ran at the audit commit `60f5cd4018b259c7f483984baf288e500732c493`; the final code commit adds only auditor tests/fix. The production `src/` subtree is identical (`0a2dab00e04a8ef2b43076472ec5c03641feab5c`), and the final full-solution regression re-executed the W11 component tests.

## 6. Auditor

- Positive runs: reserved agreement, no-Driver terminal, cancel+release
  (B-scope quiescence), driver lifecycle, late-release fencing with a newer
  reservation, and recognized pending cleanup (mid-flight observation).
- Statuses: AUD-01/02/04/06/07/08 PASS on applicable positive runs; AUD-03
  NOT RUN (A dependency); AUD-05 NOT RUN (C dependency); empty/unreachable
  controls produce NOT RUN/BLOCKED.
- Negative controls: 14 isolated controls; every seeded corruption detected;
  constraint-enforced guards demonstrated without bypass.
- Authority: Driver and Matching DBs are B-owned; Trip DB is real and used for
  cross-service agreement; Payment DB is absent.
- A's PR #43 TripInvariantAuditor was reviewed for the PASS / LOCAL_PROXY_PASS / NOT RUN
  discipline. B's auditor is independently implemented in `tests/Audit/`; A's branch was
  not copied. No proxy result is promoted to PASS.

## 7. G4 and security

- `g4/coverage-matrix.{md,json}` covers FUN-02/03/05/11, CON-01/02/04/05/06,
  REL-01/02/03/12/13/14, DSI-001..007, DSI-015..023, DSI-030/031/033,
  DSL-001/003 and the seven architectural prohibitions.
- Overall result: **FAIL/BLOCKED**. SEC-AUTH-01 Critical is open; SEC-AUTH-02/03
  High and SEC-AUTH-04 Medium are open; B-REC-01 (no production Matching
  reserve/reconcile trigger) leaves REL-01/REL-02/DSL-001 PARTIAL; DSI-021 is
  NOT RUN.
- `security/auth-remediation-handoff.md` records the proposed shared contract
  and owner split (A Identity, C Gateway/network, B enforcement after approval).
- `cross-team-handoff.md` records B-W12-10 with `HANDOFF DELIVERED`, `HUMAN
  REVIEW PENDING`, `PEER RERUN PENDING`.

## 8. Controlled boundaries / limitations

- Auditor PASS is bound to the owning authority; proxy evidence cannot become PASS.
- AUD-03/05 are owner dependencies, never PASS.
- AUD-08 ages Driver/Matching Outbox/attempt rows; Trip Saga is A-owned.
- Reconciliation tracing requires a caller-supplied workflow/consumer span;
  traceparent is not persisted in the Matching ledger.
- W11 REL-01/REL-02 recovery uses an explicit, Testing-only reconcile trigger;
  the production Matching runtime has no reserve execution or unattended
  restart/redelivery reconcile trigger (B-REC-01). Unattended recovery is not
  claimed, and REL-01/REL-02/DSL-001 are PARTIAL.
- Redis is a derived projection, never reservation authority.
- No authenticated principal contract exists; no authorization PASS is claimed.

## 9. CI status

`NOT TRIGGERED` for PR #45: `.github/workflows/ci.yml` `pull_request.branches`
does not include the W11 base. Zero check runs is not green; a billing-blocked
run would also not be green. The shared workflow is leader-owned and was not
modified.

## 10. Artifact hashes

`hashes.txt` records SHA-256 for every deliverable in this bundle.
