# TEAM W13 controlled performance evidence

All three experiments meet the committed protocol and scoped acceptance checks. All 24 canonical task rows are verified. Final exact-tree GitHub CI, merge/protection checks and fresh post-merge main CI remain separate weekly acceptance steps; see [TEAM PR #60](https://github.com/minhqng/ride-hailing-microservices-system/pull/60) for live state.

Measured source: `d291f0430faa389e4351377ce4db361aa681648b`, tree `87222fee80d5ce35cfc351b7a0f5e8c5cdac667d`, on verified W12 main `421c0595cdb033759bbdeb88c09bc97bc46c4de6`. [Current-source G4 CI](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/41456434549) passed all four jobs: 1207 regression cases passed, zero failed, one ordinary opt-in Identity theory skipped; seven actual Identity negative cases separately executed without skips. Three real G4 journeys, 55 HTTP security checks and AUD-01..08 passed.

## Scope and controls

See [protocol](protocol.json), [image freeze](image-freeze.json), [reported metric definitions](reported-metrics.json), [complete analysis](analysis/campaign-analysis.json), [task accounting](task-status.md) and [member matrix](member-progress.csv). All experiments have three independent repetitions at every canonical point and one observed/configured image, host and resource fingerprint per experiment. The six application images were built once and reused with `--no-build`; all observed cgroup limits matched Compose. PostgreSQL 18.6, RabbitMQ 4.3.6, Redis 8.10.2 and k6 0.54.0 were fixed. Host: Ryzen 5 6600H, Docker Desktop Linux containers, 12 logical CPUs and approximately 7.9 GB VM memory; exact observations are in every manifest. Prefetch 16 and DB pool 20 per instance stayed fixed. Increasing consumer replicas increases total application CPU/pool capacity while the physical host is fixed; this is explicitly an application replica comparison. No retry setting was changed.

The [rejected predecessor](rejected-campaigns/source-005-image-drift/README.md) is preserved because image identities drifted between rebuilds. Its setup-invalid measurements are not used in the accepted summaries. No failed system result was selected away.

## E01: authenticated Driver contention

Three repetitions at each N; 558 real HTTP requests, 15 winners and 543 expected conflicts. Every point has exactly one durable winner and reserve intent, followed by exact authenticated release and an independent read-only authority check. Latency excludes preconnection. The table gives the median of the three per-run quantiles and the range of per-run P95 values.

| Contenders | Requests across 3 runs | Median P50 ms | Median P95 ms | P95 run range ms |
|---|---:|---:|---:|---:|
| 1 | 3 | 7.1 | 7.1 | 6.9–7.5 |
| 10 | 30 | 14.7 | 20.4 | 19.8–27.9 |
| 25 | 75 | 30.3 | 38.7 | 38.6–44.6 |
| 50 | 150 | 23.4 | 39.5 | 37.6–47.8 |
| 100 | 300 | 46.3 | 76.7 | 67.9–77.1 |

N=1 provides only three request observations; its P95 is not a population latency or SLA estimate. These latencies include expected domain-conflict responses. No optimization or architecture change is justified by this small local contention result alone.

## E02: focused authenticated public intake

Twelve 30-second constant-arrival-rate runs used ten real disposable Rider accounts plus a separate warmup/preflight Rider. All 16,661 requests were classified, with zero dropped iterations, system errors or authentication failures. Every successful request matches one Trip/idempotency resource. Independent owning-database audits show terminal no-driver closure, no assignments, no spurious Saga/Payment effects and drained Outboxes/cleanup.

| Offered requests/s | Requests | Successes | Expected conflicts | Median actual requests/s | Median P50 ms | Median P95 ms | P95 run range ms |
|---|---:|---:|---:|---:|---:|---:|---:|
| 10 | 903 | 429 | 474 | 10.03 | 4.3 | 15.7 | 14.8–15.8 |
| 25 | 2253 | 488 | 1765 | 25.02 | 4.0 | 13.0 | 12.9–13.4 |
| 50 | 4502 | 475 | 4027 | 50.03 | 3.8 | 12.0 | 11.9–13.1 |
| 100 | 9003 | 699 | 8304 | 100.02 | 3.5 | 11.4 | 11.1–11.6 |

This focused workload has no Driver fleet. It measures real authenticated intake and automatic no-driver resolution, including the active-Trip guard. Higher offered rate increases the share of fast expected conflicts, so falling aggregate latency is not evidence that full rides or Payment scale faster. Request throughput is not completed-ride throughput. G4 independently exercises successful rides, cancellation/void and terminal-decline/release.

## E03: asynchronous intake replicas

Nine runs produced 3,600 unique durable intakes and 3,960 actual deliveries/ACKs, including 360 deliberate duplicate deliveries without duplicate durable rounds. Every actual replica consumed work; broker consumer counts and unique telemetry identities matched the real containers. The autonomous worker was disabled to isolate durable intake, and every pending round retained its production dispatch metadata. Pending work is not claimed as completed rides.

The offered rate was 20 unique messages/s; deliberate duplicate injection raises physical offered deliveries to approximately 22/s. Completed-delivery observations below use exact broker ACK totals divided by the complete observed interval, including management-statistics visibility delay. They are conservative observed rates, not maximum broker/application capacity.

| Consumers | Unique intakes across 3 runs | Actual deliveries/ACKs | Median observed ACK/s | ACK/s run range | Median stored-creation proxy P95 ms | Proxy P95 run range ms |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 1200 | 1320 | 17.5 | 17.5–18.3 | 43.2 | 42.4–44.1 |
| 2 | 1200 | 1320 | 18.2 | 17.5–18.2 | 42.5 | 42.1–43.7 |
| 4 | 1200 | 1320 | 17.5 | 17.5–17.5 | 42.4 | 42.1–43.0 |

Each proxy quantile has 400 unique measured intakes per run. PostgreSQL created_at is the stored creation timestamp, not commit or ACK time; these values are an intake-stage proxy. Actual shared consumer processing histograms are preserved as supporting telemetry with their declared sliding windows, which can overlap warmup. The legacy achievedConsumeRatePerSecond metadata uses publication/queue-observation timing and is not used here as completed-delivery throughput. At this offered load no SUT saturation bottleneck or capacity benefit from additional replicas is established. No unsupported tuning or linear-scaling claim follows.

## Cross-owner support and provenance

[Exercised runtime observations](final-state/runtime-observations.json), from [CI 41464187181](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/41464187181), preserve actual Payment/consumer cumulative metrics, an empty live Payment queue with consumers, and read-only PostgreSQL statistics after the real G4 journeys. They contain five Payment operations (two successful authorizations, one decline, one capture, one void), four provider effects and zero observed database deadlocks. This is functional observability support, not a Payment benchmark. It complements E02’s explicitly idle Payment baseline. The full G4/security snapshots and CI artifact digests are retained beside it.

[Data provenance](data-provenance.json) lists 99 curated files and hashes for 114 original raw files. The curator independently checks complete sample counts, outcome totals, k6 quantiles, Driver state/cleanup and E03 input/row/ACK identities. Complete measured samples, normalized tool summaries, selected observed metric series, queue samples and owning-state proofs are portable in this bundle. Full engine streams remain local-only under the repository hygiene policy; their exact paths, byte sizes and SHA-256 hashes are retained. No credentials or bearer tokens are evidence.

For independent data curation on the retained raw files, run:

```bash
python scripts/evidence/performance-curate.py --runs-root evidence/team/w13-integration/runs --output evidence/team/w13-integration
```

Analyze the committed manifests on the preserved integration branch (the certificate checks source ancestry):

```bash
export W13_G4_EVIDENCE=evidence/member-b/w13-performance-campaign/g4/team-integration-readiness.json
python scripts/perf/w13.py analyze --experiment ALL --runs-root evidence/team/w13-integration/runs --out-dir evidence/team/w13-integration/analysis
```

To reproduce new measurements, use an isolated checkout of the measured source, initialize ignored auth credentials, build all required images once using the protocol command, set W13_USE_PREBUILT_IMAGES=1, select a new empty W13_RUNS_ROOT, and run every command in protocol.json. Exact latency may vary; expected correctness outcomes must remain stable.

Canonical ownership is A (8 tasks), B (six Doc 05B §22.2–22.7 sections; no invented B-W13 IDs) and C (10 tasks). Original contribution commits are retained. Integration corrections, measurements and acceptance closeout are integrator work; no peer rerun or member-authored evidence is fabricated. Human review is explicitly waived by project-lead instruction, while GitHub protections and every technical gate remain mandatory.

W14 E04/recovery/HA execution is not accepted by this W13 evidence. W15/W16 are excluded.
