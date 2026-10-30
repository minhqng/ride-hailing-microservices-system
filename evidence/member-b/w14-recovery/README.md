# Controlled recovery proof

**Classification: functional smoke only. Report-grade E04 is NOT ACCEPTED.**
Verified source: `212c98fbcf4f3c1056e9f0dd9017af8b2a7e3a2f`, tree
`fb5189ab857ec6c415215333d5fb2d7e620e1a14`. This bundle retains the minimum
source-bound raw inputs for one real-provider run, not a benchmark campaign.

The [runbook and capability ledger](../../../scripts/perf/README-w14.md)
define ownership, methodology, commands, validity and C's HA integration
contract. From the repository root, run `bash evidence/member-b/w14-recovery/reproduce.sh`
in a native Linux checkout or **Git Bash on Windows**. The producing Windows
worktree uses Windows Git metadata paths; the WindowsApps/WSL `bash` alias
cannot resolve those paths. With a standard Git for Windows installation,
PowerShell can select Git Bash explicitly:

```powershell
& "$env:ProgramFiles/Git/bin/bash.exe" evidence/member-b/w14-recovery/reproduce.sh
```

| Observation | Measured functional run |
|---|---:|
| Unique confirmed publications | 14 |
| Explicit duplicate replay | 1 |
| Queue/application samples | 58 |
| Observed outage λ | 0.481139 messages/s |
| Observed recovery λ | 0.517726 messages/s |
| Requested / observed fault window | 8 / 8.313597 s |
| λT prediction / peak ready | 4 / 4 messages |
| Peak unacked / outstanding business work | 0 / 5 |
| Effective recovery μ | 1.035452 terminal outcomes/s |
| μ interval range | 0–9.755639 outcomes/s |
| Predicted drain, using outstanding work | 9.657622 s |
| Observed return to sampled steady state | 8.885682 s |
| First observed successful processing after restoration request | 2.999238 s |
| Confirm latency P50 / P95, n=14 | 11.4147 / 135.5074 ms |
| Broker redelivery delta in this run | 0 |
| Duplicate business effects | 0 |
| Repeat count / across-run variance | 1 / unavailable |

The producer continued during recovery. Ready backlog and durable application
obligations are separate: the peak outstanding count includes the existing
database obligation. Every confirmed identity has one terminal `NO_DRIVER`
round and one confirmed/published `MatchingFailedV1` Outbox identity. The real
OFFLINE Driver is byte-identical before/after, with no fabricated attempt or
reservation. The run-dataset B12 auditor reports AUD-01/02/06/08 PASS; the
A/C-dependent AUD-03/04/05/07 legs remain NOT_RUN.

The simple drain formula differs from observed steady state because service
rate is visibly bursty, the worker advances durable stages independently of
intake, and observation has polling/broker-statistics delay. Broker registration
was observed later than the first successful processing: the cached management
counter is a delayed upper bound on reconnect, not its exact physical time.
Final quiescence/publication observations are proof barriers, not precise
per-message completion timestamps. No bottleneck or production capacity is
inferred from this small functional dataset.

REL-15 B proofs additionally use real commit-before-ACK connection loss,
unacked redelivery, duplicate convergence, Matching process kill/restart,
and a separately owned real RabbitMQ stop/return with the same live
application reconnecting. Driver reservation and Outbox identities remain
singular. Existing Driver relay tests prove failed publication remains PENDING
without a forged published timestamp, then recovers through real RabbitMQ.

Redis proof uses real PostgreSQL and Redis behind a TCP partition. It asserts
degraded discovery, failed projection update, targeted deletion, explicit event
replay using the original session, stale version/sequence/foreign-session
rejection and unchanged Driver authority. This is event replay, not automatic
full rebuild. Full provider suites also cover ingress and UNKNOWN/stage safety.

G4 remains RED. Live fetched `main` is `86062af`; B-W11/W12/W13 are
`3ba3724` / `61d7328` / `16dc175`. PRs #44, #45, #43 and #41 remain open;
B-W13 has no PR. Main's Identity/Gateway remain skeletons; A's current gate
still records pending team acceptance. C's `d300849` branch has no executable
three-node `ha` profile. REL-16 team acceptance is SHARED_DEPENDENCY; no node
loss was executed or accepted by B. The adapter is verified with explicit
synthetic negative controls, never relabeled as real HA measurement.

W13 report-grade E01/E02/E03 remain gated. Their acceptance is a separate G5
obligation, not an invented prerequisite to W14 tooling. E04 report execution
refuses with exit 3 while G4 is RED; a future report campaign requires at least
three complete real repetitions and the actual release/experiment gates.

Evidence inputs: [manifest](manifest.json), [analysis](analysis.json),
[live gate evaluation](g4-readiness.json), and
[functional run](functional-smoke/manifest.json). Per-file hashes bind the
retained receipts, samples and final state. Generated build/test logs remain
under ignored output paths; no console dumps or session reports are committed.
