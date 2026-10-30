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

Report-grade execution is refused unless `evidence/member-b/w13-performance-campaign/g4/g4-readiness.json`
declares `verdict: GREEN`, every required check is `PASS`, the certified source
is an ancestor of the current W13 source, and `src/`/`tests/` have not changed
since certification. A smoke run may execute the harness, but its analysis
verdict is never `ACCEPTED`.

Current state (2026-10-30): **G4 RED** — `SEC-AUTH-01..04` are open, the
shared Identity/Gateway authentication is a skeleton, A/C acceptance is
unmerged, and the team-level auditor is not green. E02 is therefore blocked at
the authenticated-API preflight before any load is generated.

## Isolation and reset

All commands use:

```bash
docker compose -f compose.yaml -f scripts/perf/compose.w13.yaml ...
```

The override fixes the project name to `ride-hailing-w13`, remaps every host
port, removes the Matching host port so replicas can be scaled, and freezes
CPU/memory limits plus the PostgreSQL pool size. It never touches the normal
development stack.

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

# E02 readiness boundary (blocked while shared auth is absent)
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

Secrets are never recorded: password/token/secret environment keys are
redacted, and E02 token files are mounted into the k6 container from a
gitignored run path.

## Outcome and latency discipline

- Outcome classes are always separate: `success`, `domainConflict`,
  `systemError`, `timeout`, `unexpected`. A `409` is never counted as a system
  error.
- Percentiles are nearest-rank P50/P95 with the sample count. The analyzer
  does not report a percentile without `n`.
- E02 uses k6's open model and reports configured rate, achieved rate,
  `dropped_iterations`, allocated/active VUs, and domain conflicts separately
  from transport/system failures.
- E03 proves replica registration from the broker consumer count and actual
  work distribution from each replica's consumption log. Starting containers
  is not proof.
- The autonomous Matching worker is disabled in E03 by default (recorded in
  the manifest) so consumer count is the only scaled capacity variable.
  `--worker-mode enabled` is a separate, documented confounded variant.
- Every run also records an authoritative final-state artifact. Report-grade
  campaigns must rerun the B-owned invariant auditor (`w13.py audit`) and
  record `auditor.status = PASS` before analysis can accept the campaign.

## Dependencies that remain with A/C

- A: authenticated Rider principal, Gateway routing, Trip/Saga acceptance and
  Trip-side metrics under load.
- C: token issuance/validation, Gateway network boundary, RabbitMQ replica
  management and shared broker metrics, Payment/platform acceptance.
- Lead: G4 approval, peer reruns, CI triggering for the stacked base.

W14 (`E04` backlog recovery, `REL-16` quorum node loss) is explicitly out of
scope here.

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
