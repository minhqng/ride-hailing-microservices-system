# W13 performance experiment harness

Canonical scope: Docs 03 §17–§25, Doc 04 §24, Doc 05B §22. The harness prepares
and runs three experiments against the real Compose topology:

| Experiment | Question | Independent variable |
|---|---|---|
| `E01` | Can one Driver authority resolve simultaneous reservations correctly, and at what latency cost? | contenders `N = 1, 10, 25, 50, 100` |
| `E02` | How does the canonical authenticated API behave under a fixed offered rate? | k6 `constant-arrival-rate` at `10/25/50/100` req/s |
| `E03` | How does the Matching intake consumer scale with replica count? | consumers `1, 2, 4` |

The harness never changes service behavior, never substitutes a Testing-only
or unauthenticated demo route for a production API claim, and never invents a
measurement that was not executed.

## G4 gate

Report-grade execution is refused unless the curated G4 evidence under
`evidence/member-b/w13-performance-campaign/g4/` declares `verdict: GREEN`,
every required check is `PASS`, the certified production/test source is an
ancestor of the current W13 source, `src/`/`tests/` have not changed since
certification, and the working tree outside `evidence/` is clean. The analyzer
re-evaluates the live gate instead of trusting manifest booleans, and ignores
attempts to lower the canonical repeat/rate floors. A smoke run may execute the
harness, but its analysis verdict is never `ACCEPTED`.

The original member certificate records the historical G4 RED result. TEAM W12
subsequently integrated real Identity/Gateway/service authentication and passed
G4 on main `421c059`. W13 changes production source, so report-grade execution
still requires a new certificate backed by current-source checks. Select that
certificate with `W13_G4_EVIDENCE`; preserve the historical certificate.

## Isolation and reset

All commands use:

```bash
# Generate disposable ignored credentials before starting the stack
python scripts/security/init-local-auth.py

docker compose --profile obs -f compose.yaml -f scripts/perf/compose.w13.yaml ...
```

The override fixes the project name to `ride-hailing-w13`, remaps every host
port, removes the Matching host port so replicas can be scaled, and freezes
CPU/memory limits plus the PostgreSQL pool size. It never touches the normal
development stack.

The same bounded collector, Prometheus and Jaeger services run for every
configuration. Application metric export is fixed at five seconds. E02/E03
preserve actual Prometheus range samples for the measured interval; E01 retains
an authoritative post-contention Outbox snapshot. Matching's existing intake
records dedup only after database commit, and shared ACK metrics only after ACK.
E02 warms ten concurrent requests from the separate preflight Rider every three
seconds for 30 seconds; E03 warms its broker intake for 30 seconds at the fixed
offered rate. Both wait for the declared finite metric series
(bounded at 75 seconds) before measurement so Prometheus rate windows have a
real baseline. Measured range endpoints exclude later evidence collection.

Report-grade campaigns build application images once before the first measured
reset, record their identities, then set `W13_USE_PREBUILT_IMAGES=1`. Every reset
uses `--no-build`; a missing image fails instead of silently rebuilding. The
analyzer requires the same actual image identities across repetitions. Build:

```bash
docker compose --profile obs -f compose.yaml -f scripts/perf/compose.w13.yaml build gateway identity driver matching trip payment
```

`reset` destroys only this compose project's volumes:

```bash
python scripts/perf/w13.py reset --yes
```

Each measured run resets the disposable volumes first. E01/E02 do this per
repeat; E03 does it per replica configuration. Raw data never flows between
measured configurations.

## Commands

```bash
# Inspect the gate
python scripts/perf/w13.py g4

# E01 harness smoke (functional only)
python scripts/perf/w13.py e01 --mode smoke --n 1,2,10

# E02 with ten real disposable Rider accounts plus a separate preflight Rider
python scripts/perf/w13.py e02 --mode smoke
# Optional: point at the trip host and provide a token file to test the boundary
python scripts/perf/w13.py e02 --preflight-base-url http://127.0.0.1:18103 \
  --token-file /path/to/tokens.txt --mode smoke

# E03 consumer-scaling harness smoke
python scripts/perf/w13.py e03 --mode smoke --replicas 1,2 --rate 5 --duration 5s

# Validate compose config, manifest logic and synthetic fixtures
python scripts/perf/w13.py validate

# Analyze all completed runs
python scripts/perf/w13.py analyze --experiment ALL

# Authoritative B-owned auditor, bound to the tested source
python scripts/perf/w13.py audit --out evidence/member-b/w13-performance-campaign/auditor
```

Report-grade runs use `--mode report`; they additionally require a green G4
gate and at least three repeats per configuration. E01/E02/E03 then must cover
the canonical points; the analyzer returns `INCOMPLETE` if any group is
missing.

Exit codes: `0` PASS, `2` FAIL/INVALID/INCOMPLETE, `3` BLOCKED/gate refusal.

## Environment freeze

Every run manifest records, separately:

- configured values: compose CPU/memory limits, pool size, prefetch, worker
  mode, ports, count, rate, duration, seed, reset policy;
- observed values: Docker/Compose versions, `.NET` SDK, Python, k6 image,
  PostgreSQL/Redis/RabbitMQ versions, running containers, `NanoCpus`/memory
  from `docker inspect`, DB connections, queue state, runtime container stats;
- source binding: commit, tree, branch, dirty flag, status digest;
- G4 gate outcome and blockers;
- workload, warm-up, measurement, cooldown, dataset seed.

Password/token/secret/private-key/PASS environment keys and connection-string
passwords are redacted. E01 registers real Driver owners and obtains scoped
Matching/Trip service grants outside measurement. E02 registers a fresh Rider
cohort after every reset, with a separate preflight Rider. k6 token files live
under ignored `plans/.w13-credentials/`, outside artifacts, and are removed after
the k6 process exits. k6 receives only its script, result directory and Rider
token file; the repository and Identity private key are not mounted. Token
issuance latency is excluded from API measurements.

## Outcome and latency discipline

- Outcome classes are always separate: `success`, `domainConflict`,
  `systemError`, `timeout`, `unexpected`. A `409` is never counted as a system
  error.
- E01/E03 percentiles are nearest-rank P50/P95 over request/message samples.
  E02 reports k6 summary trend percentiles per outcome with the trend count;
  group values are medians of per-run percentiles, not pooled samples. The
  analyzer does not report a percentile without `n`.
- E02 uses k6's open model and reports configured rate, achieved rate,
  `dropped_iterations`, allocated/active VUs, per-outcome latency trends, and
  domain conflicts separately from transport/system failures. The target is
  restricted to the canonical `/trips`; report-grade mode requires the Gateway
  target, an observed rejected no-token mutation, and at least two rider
  tokens (the preflight token is excluded from the load pool).
- The default E02 cohort has no Driver fleet. Accepted creations resolve through
  actual Matching/broker paths to MATCH_FAILED. A separate read-only oracle
  checks every Trip/round, assignment absence, idle Payment/provider state,
  complete Outbox drain and cleanup. This measures public intake and no-driver
  resolution; G4 separately exercises successful rides and Saga compensation.
- E03 proves replica registration from the broker consumer count and actual
  work distribution by matching measured trip identities in each replica's
  consumption log. Starting containers is not proof, and warm-up messages are
  excluded from the distribution count.
- Matching replicas use their container hostname as the unique telemetry
  instance identity. E03 intake latency rejects negative/non-finite samples;
  its observed consumption rate includes the measured publish and drain time.
- `scripts/evidence/performance-snapshot.py` requires every declared series to
  exist and be finite. The default declares all queries; `--queries` records an
  explicit experiment scope. Missing idle counters are an incomplete snapshot,
  never fabricated zero-valued observations.
- The autonomous Matching worker is disabled in E03 by default (recorded in
  the manifest) so consumer count is the only scaled capacity variable.
  `--worker-mode enabled` is a separate, documented confounded variant.
- Every run records a separate read-only owning-database oracle and its actual
  `auditor.status`: E01 Driver contention/cleanup, E02 cross-owner no-fleet
  closure, E03 durable intake/dedup/work conservation. Pending E03 work is not
  claimed as completed rides. Current-source G4 and the B-owned auditor fixture
  suite (`w13.py audit`) separately prove the exercised business/failure paths.

## Team acceptance dependencies

- Current-source Trip/Saga, Driver/Matching, Payment/platform and security G4
  verification, independent authoritative audit and required metrics under load.
- Repeated report-grade measurements, current-tree CI and post-merge main CI.
- Human reviews/reruns are waived by the project lead for this campaign. This
  waiver does not represent a performed review or change technical gates.

W14 (`E04` backlog recovery, `REL-16` quorum node loss) is documented separately
in [the recovery runbook](README-w14.md), reusing these helpers without changing
E01–E03 measurement semantics.

## Synthetic fixtures

`scripts/perf/fixtures/synthetic/` contains small manifests marked
`"synthetic": true`. They exercise manifest validation, percentile math and
the "never green when inputs are missing" gate. The analyzer converts any
otherwise-passing synthetic campaign to `SYNTHETIC_ONLY`; no fixture can
produce acceptance.

Run the parser/analyzer tests with:

```bash
python -m unittest discover -s scripts/perf/tests -v
```
