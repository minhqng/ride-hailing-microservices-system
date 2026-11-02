# W13 canonical task accounting

Owner labels are canonical assignments. Integration fixes, measurements and closeout were executed by the integrator; original member author commits are preserved. No member-authored peer execution is fabricated.

| Owner | Canonical task | Deliverable | Status | Evidence / boundary |
|---|---|---|---|---|
| A | A-W13-01 | Add Trip final-state assertions to load fixtures | DONE_VERIFIED | E02 final-state snapshots: exact successful-request/idempotency counts, no active-Rider uniqueness violation, all Trip/Matching authorities closed. |
| A | A-W13-02 | Expose Trip/Saga/Outbox metrics for E02/E04 | DONE_VERIFIED | Trip meter, durable Saga count/age observer, selected finite E02 metrics; real PostgreSQL seeded two-minute backlog test. |
| A | A-W13-03 | Support E02 controlled offered-load runs | DONE_VERIFIED | 12 accepted E02 runs: all four canonical rates, 3 independent resets each, real Identity/Gateway/providers. |
| A | A-W13-04 | Support E04 backlog/recovery | DONE_VERIFIED | Exposed and verified Outbox pending/age and durable Saga pending/age. Actual E04 disruption/recovery acceptance belongs to W14. |
| A | A-W13-05 | Run auditor after selected performance runs | DONE_VERIFIED | Every E02 run has a separate read-only cross-authority no-fleet audit; current-tree G4 additionally exercises successful/compensating Saga paths. |
| A | A-W13-06 | Review bottleneck interpretation | DONE_VERIFIED | reported-metrics.json and README: generator-limited observations, proxy/window definitions, no unsupported scaling/SLA/optimization claim. |
| A | A-W13-07 | Preserve run metadata/raw results | DONE_VERIFIED | 24 source-bound manifests, 99 curated files, 114 original raw-file hashes; fixed image/host/config fingerprints. |
| A | A-W13-08 | Report-note draft | DONE_VERIFIED | README and reported-metrics.json with actual counts, quantiles, run dispersion and interpretation limits. |
| B | Doc 05B §22.2 | Experiment preparation | DONE_VERIFIED | Committed protocol revision 2, real auth and fixed resource/image/prefetch/pool controls; rejected predecessor preserved. |
| B | Doc 05B §22.3 | PERF/E01 — contention curve | DONE_VERIFIED | E01 ACCEPTED: 3 repetitions at N=1/10/25/50/100, 558 requests, 15 winners/543 conflicts; exact cleanup and independent Driver state proof. |
| B | Doc 05B §22.4 | PERF/E02 — controlled arrival-rate API | DONE_VERIFIED | E02 ACCEPTED: 12 runs/16661 requests, all four rates, exact offered starts, no dropped/system/auth errors; complete authority closure. |
| B | Doc 05B §22.5 | PERF/E03 — async consumer scaling | DONE_VERIFIED | E03 ACCEPTED: 9 runs, 3600 unique intakes, 3960 actual deliveries/ACKs, 360 deliberate duplicate deliveries with no duplicate durable round. |
| B | Doc 05B §22.6 | Analysis | DONE_VERIFIED | Per-run and grouped medians/min/max/sample standard deviations; sample counts and semantic outcomes remain separate. |
| B | Doc 05B §22.7 | Evidence packaging | DONE_VERIFIED | Portable curated samples/summaries/metrics/final-state evidence plus original raw hashes and exact commands/source/image provenance. |
| C | C-W13-01 | Freeze Rabbit prefetch/config for experiment baseline | DONE_VERIFIED | RabbitMQ 4.3.6, prefetch 16, per-instance DB pool 20, source/config and actual container image/resource identities unchanged across comparisons. |
| C | C-W13-02 | Support E03 consumer replica counts | DONE_VERIFIED | E03 actual broker consumer counts 1/2/4, every replica processes work; telemetry instances equal distinct actual container hostnames. |
| C | C-W13-03 | Capture queue ready/unacked metrics | DONE_VERIFIED | All measured queue-ready/unacked range samples preserved and finite; exact delivery/ACK totals independently observed. |
| C | C-W13-04 | Capture consumer processing/redelivery/dedup | DONE_VERIFIED | Declared consumer processing/redelivery/dedup Prometheus data, exact ACK totals, explicit duplicate input and independent durable uniqueness proof. |
| C | C-W13-05 | Capture Payment DB/consumer metrics | DONE_VERIFIED | E02 records idle Payment owning-state/queue baselines. CI runtime-observations adds actual exercised Payment/consumer counters and read-only Payment DB statistics after real G4 journeys. |
| C | C-W13-06 | Validate no retry-config changes across compared runs | DONE_VERIFIED | One measured source and one complete observed/configured image/host/resource fingerprint per experiment; no retry configuration change. |
| C | C-W13-07 | Review system-error vs domain-conflict categorization | DONE_VERIFIED | Raw response/metric classifications checked against curated samples and tool totals; expected 409 conflicts remain separate from system/auth failures. |
| C | C-W13-08 | Support 3–5 measured runs | DONE_VERIFIED | Three independent measured repeats at every canonical point; all 24 runs preserved. |
| C | C-W13-09 | Preserve raw broker/OTel evidence | DONE_VERIFIED | Selected broker/OTel series and queue samples are curated; all original raw engine files are retained locally with exact byte hashes per hygiene policy. |
| C | C-W13-10 | Draft messaging performance analysis | DONE_VERIFIED | README and reported-metrics.json explain offered-rate limits, broker ACK visibility delay, duplicate suppression and supported conclusions. |
