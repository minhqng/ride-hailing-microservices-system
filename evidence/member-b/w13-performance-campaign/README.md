# W13 performance harness and gated experiment readiness

**Verdict: `W13 HARNESS READY — PERFORMANCE ACCEPTANCE GATED`**

This bundle records the complete W13 E01/E02/E03 experiment infrastructure,
its functional smoke validation, and the independent G4 readiness assessment.
No report-grade benchmark was executed because G4 is red.

## Source binding

| Item | Value |
|---|---|
| Branch | `feature/B-W13-performance-campaign` |
| Base | validated W12 head `61d7328838befa0adbbc9974a7b5c1635cdecf2d` (PR #45) |
| Code commit tested by the smoke runs | `1dcc7ee0f7b7c1575cbe4cbddec6a6211a097a58` |
| Code tree | `4a0dd7b805b5ed15c7f3e067a0b127135e614e89` |
| Report-grade | **NO** |
| Acceptance | **NO** |

The branch is stacked on W12. W11 (`3ba3724`, PR #44) and W12 (PR #45) remain
untouched; no A/C-owned source changed. The W13 code commits change only
`scripts/perf/` plus a Python `__pycache__` ignore rule.

## Live topology observed

| Ref | Commit | Note |
|---|---|---|
| `origin/main` | `86062af` | docs-only attribution correction |
| W11 / PR #44 | `3ba3724` | unchanged |
| W12 / PR #45 | `61d7328` | latest validated B head, recovery repair included |
| A-W12 | `132ecfd` | unmerged; G4 matrix says team G4 not green |
| C-W10/W11 | `d300849` | unmerged; shared platform/REL-11 pending |
| B-W13 | `98eb274` + `e8feb2f` + `1dcc7ee` | W13 harness on top of `61d7328` |

## G4 readiness — RED

Independently rechecked from live source and A/C evidence. The curated
machine-readable assessment is [g4/g4-readiness.json](g4/g4-readiness.json).
The harness refuses `--mode report` while this remains red.

Required check | Result | Blocker
---|---|---
B-owned correctness | PASS | B-REC-01/DSI-021 repaired forward; auditor green for B scope
Security baseline | **FAIL** | `SEC-AUTH-01` Critical; `SEC-AUTH-02/03` High; `SEC-AUTH-04` Medium all OPEN
A correctness acceptance | **FAIL** | A-W12 G4 matrix says not green; PR #43 unmerged
C correctness acceptance | **FAIL** | C-W10/W11 PR #41 unmerged; REL-11 shared DLQ pending
Authoritative auditor | **NOT RUN (team)** | B auditor PASS; AUD-03 (A) and AUD-05 (C) not run
Production API surface | **FAIL** | Identity/Gateway skeletons; no token issuance or routing
Broker surface | PASS | RabbitMQ 4.3.6, frozen exchanges/queues present; C replica metrics still external
Stable environment | PASS | Docker/Compose and fixed W13 limits verified

Additional blockers: A/C peer reruns and human sign-off absent; PR #45 has zero
CI checks on its stacked base, and zero checks is not green.

## Harness

| Path | Purpose |
|---|---|
| `scripts/perf/w13.py` | CLI: `g4`, `env`, `up/down/reset`, `e01/e02/e03`, `analyze`, `audit`, `validate` |
| `scripts/perf/w13lib.py` | Environment/source capture, manifest validation, percentiles, Queue/Rabbit/psql helpers |
| `scripts/perf/w13_e01.py` | Barrier-synchronized same-Driver contention runner with authoritative final-state AUD-01 |
| `scripts/perf/w13_e02.py` | Authenticated-API readiness gate and k6 `constant-arrival-rate` orchestration |
| `scripts/perf/k6/e02-create-ride.js` | Open-model `POST /trips` load with outcome classes |
| `scripts/perf/w13_e03.py` | Fixed-rate publisher, replica registration, queue metrics, per-replica work-distribution proof |
| `scripts/perf/w13_analyze.py` | Fail-closed campaign analysis: P50/P95 + n, ranges, achieved vs configured, conflict/error split |
| `scripts/perf/compose.w13.yaml` | Isolated `ride-hailing-w13` project, remapped ports, CPU/memory and pool freeze |
| `scripts/perf/schema/w13-manifest.schema.json` | Manifest contract |
| `scripts/perf/fixtures/synthetic/` | Explicitly synthetic parser/analyzer fixtures; can never produce acceptance |

Operator instructions are in [`scripts/perf/README.md`](../../../scripts/perf/README.md).

## Functional smoke results (NOT report-grade)

All smoke manifests are `reportGrade=false`, `acceptance=false`, and bound to
the clean code commit above. Curated data: [smoke-summary.json](smoke-summary.json).
Raw per-request files remain local under gitignored `runs/**/raw/`.

### E01 — contention harness (SMOKE_PASS)

| N | winners | expected conflicts | system errors | samples | P50 ms | P95 ms | AUD-01 |
|---:|---:|---:|---:|---:|---:|---:|---|
| 1 | 1 | 0 | 0 | 1 | 7.70 | 7.70 | PASS |
| 2 | 1 | 1 | 0 | 2 | 8.39 | 16.39 | PASS |
| 10 | 1 | 9 | 0 | 10 | 16.96 | 23.61 | PASS |

Every measured Driver ended `RESERVED` with exactly one active reservation,
one matching `DriverReservedV1` Outbox intent, and no lifecycle effect. Barrier release spread was 0.09–0.89 ms at N=2/10; connections were established before
the barrier. `N=25/50/100` and repeat runs were **NOT RUN** because G4 is red.

### E02 — controlled API load (BLOCKED)

- Run: `W13-E02-20261030T031621Z-05167996`, status `BLOCKED`.
- Reason: `AUTH_TOKEN_UNAVAILABLE`; no shared token issuer exists.
- Gateway `POST /trips` (host `:18080`) returned **404** — the Gateway is a
  skeleton with no routing.
- Trip direct `POST /trips` (`:18103`) returned **401 UNAUTHORIZED** — Trip
  fails closed without a principal, but no authenticated principal can be
  obtained.
- k6 was **NOT RUN**. The `10/25/50/100 req/s` open-model rates, dropped
  iterations, VUs and achieved rates are therefore **NOT RUN**, not zero.
- The harness refuses to substitute `/internal/test/...` or another
  unauthenticated/Testing-only route.

### E03 — consumer scaling harness (SMOKE_PASS)

The Matching autonomous worker was disabled by default to isolate the intake
consumer path (recorded in each manifest). A fixed-rate publisher fed
`matching.ride-requested.q`; each configuration resets the disposable volumes.

| Replicas | measured messages | rounds created | achieved send rate | P50 intake ms | P95 intake ms | per-replica consumed log lines | broker consumers | attempts |
|---:|---:|---:|---:|---:|---:|---|---:|---:|
| 1 | 25 | 25 | 5.19/s | 20.36 | 46.83 | 40 | 1 | 0 |
| 2 | 25 | 25 | 5.18/s | 22.27 | 26.55 | 20 + 20 | 2 | 0 |

The extra log lines are the 15 warm-up messages. No duplicate rounds, duplicate
input identities, queue backlog, redelivery, or worker attempts were observed;
the queue drained in every configuration. `replicas=4` and repeat runs were
**NOT RUN** because G4 is red.

## Analysis validity

`analysis/` is generated by `w13_analyze.py` from the manifests:

- E01 `SMOKE_PASS`, E02 `BLOCKED`, E03 `SMOKE_PASS`;
- campaign verdict `NOT_ACCEPTED`, acceptance `NO`;
- P50/P95 are nearest-rank with sample counts; ranges are emitted per group;
- domain conflicts are never merged with system errors;
- a missing/invalid manifest makes the campaign `INVALID`;
- report-grade acceptance additionally requires >=3 repeats, a green G4 gate,
  a clean source tree, the canonical configuration points, and an auditor PASS.

## Authoritative auditor

`auditor/` contains a clean-source regression of the B-owned auditor
(`18/18` tests PASS; source commit/tree above). Positive scenarios:
31 PASS, 0 FAIL, 17 NOT RUN (A/C-owned), 0 BLOCKED. Negative controls:
11 expected FAIL detections, 4 expected BLOCKED honesty controls. AUD-03 (A)
and AUD-05 (C) are not run by B and are never reported as PASS.

This is a B-owned auditor regression, not a team-level auditor green and not a
G4 approval.

## NOT RUN — exact reasons

- **All report-grade E01/E02/E03 measurements**: G4 red (`--mode report` exits 3).
- E01 canonical `N=25,50,100` and 3–5 repeats: gated.
- E02 k6 runs at `10/25/50/100 req/s`, 3–5 repeats: no authenticated principal,
  no Gateway route, no token issuer.
- E03 `replicas=4` and 3–5 repeats: gated.
- W14 `PERF/E04` backlog recovery and `REL-16` quorum node loss: explicitly out
  of scope and not started.

## Remaining dependencies

- **A**: authenticated Rider principal contract, Gateway routing, Trip/Saga
  acceptance and Trip metrics under load.
- **C**: token issuance/validation, Gateway network boundary, RabbitMQ replica
  management and shared broker metrics, Payment/platform acceptance.
- **Lead**: G4 approval, A/C peer reruns, human sign-off, CI triggering for the
  stacked base.

## Hygiene

Only reusable runners, the manifest schema, seed/reset tooling, operator
instructions, curated manifests/final-state and analysis outputs are retained.
Raw per-request samples, k6 JSON points, container logs and console output stay
under gitignored `runs/**/raw/`. No benchmark summary is fabricated.

## W14 freeze

W14 E04/REL-16 remain future work. This bundle does not start or design them.
