# Backlog and recovery verification

The frozen capability ledger below follows Docs 00–02 for authority and
invariants, Doc 03 for proof and validity, Doc 04 §25 for team ownership,
and Doc 05B §23 for B's weekly deliverables. It was compiled before source
modification. Preparation and isolated correctness checks do not confer G5.

| Requirement | Canonical source | B-owned behavior | Required experiment | Authority | Required proof | Shared dependency | Acceptance gate |
|---|---|---|---|---|---|---|---|
| E04 | Doc 03 §21; Doc 05B §23.2 | Controlled arrivals, consumer interruption, recovery analysis | Fresh environment, baseline, continued publication during outage and drain | RabbitMQ queue; Matching PostgreSQL work; Driver PostgreSQL | Confirm receipts, time series, identity-complete final rows, resources, auditor, ≥3 repeats | Release source and G4 certification | G4 and experiment validity; smoke never accepted |
| REL-15 B support | Doc 03 REL-15; Doc 04 §25 | Durable intake, duplicate convergence, restart and reconnect | Real broker/consumer outage, commit-before-ACK redelivery, replay | Matching rounds/attempts/outbox; Driver reservations/outbox | No loss or duplicate effect; UNKNOWN blocks; post-recovery audit | C owns primary platform exercise; A owns Saga/Trip | B correctness separately; team G5 requires A/C proofs |
| REL-16 B support | Doc 03 REL-16; Doc 05C §33; Doc 05B §23.3 | Verify application recovery against C environment | Baseline, one-node loss with majority, return | C broker leader/member data plus B databases | Actual 3 members, leader, confirms, ACKs, B invariants | C-provided executable `ha` profile | SHARED_DEPENDENCY until real C topology exists |
| Redis recovery | Doc 01 DSI-006/007, §21, AC-09/10; Doc 05B §23.4 | Derived projection survives/reconstructs through supported event replay | Real Redis partition, targeted key deletion, replay, stale version/sequence/session tests | Driver PostgreSQL exclusively | Projection state plus unchanged Driver truth; no fabricated reservation | Retained authoritative events for manual replay | B provider correctness; no claim of automatic rebuild |
| Measurement validity | Doc 03 §§17,21,25,35; Doc 04 §§24–25 | Source-bound raw artifacts and analysis | Negative controls plus repeated real runs | Git objects, provider observations | Clean source, frozen configuration, environment, complete groups, auditor | Team G4 and release acceptance | Fail closed; synthetic fixtures never measurements |

Non-goals: A's Trip/Saga/Payment workflow, C's production broker management,
authentication architecture, W15, optional P1. No W13 acceptance prerequisite
is invented: W13 E01–E03 and W14 E04 remain separate G5 obligations. W14
reuses W13 isolation, source binding, redaction, environment capture and
statistics; W13 experiment semantics remain frozen.

## Failure scenarios

| Trigger | Invariant and discriminating proof | Severity |
|---|---|---|
| Stop consumer while producer continues | Confirmed work remains ready and later has its original intake identity | Critical |
| Kill connection after intake commit before ACK | Redelivery observes same message/round, one durable obligation | Critical |
| Duplicate after terminal result | Same attempts/reservations and one outbox business effect | Critical |
| Queue reaches zero while worker is pending | Separate broker-drained, intake-persisted and terminal timestamps | Critical |
| Broker connection shutdown with recovery disabled | Consumer supervisor reconnects instead of waiting forever | High |
| Redis unavailable/reset/stale replay | Degraded discovery, monotonic projection, unchanged PostgreSQL authority | Critical |
| Measured μ ≤ λ or changing throughput | No finite drain prediction; retain interval rates and variability | High |
| Missing/corrupt/source-mismatched evidence | Analyzer rejects even if summary says PASS | Critical |
| One quorum declaration on one node | Adapter refuses a three-node HA claim | High |

Scenario dimensions: timing, scale, state transitions, environment, error
cascades, integrity, integration, authorization and business logic. Human
personas and compliance changes add no distinct B-owned behavior here; secret
retention and forged artifact paths are covered by evidence validation.

## Provider semantics

The application uses B's `BrokerConsume` for intake, not the shared
`ManualAckConsumer`. Intake ACK follows the PostgreSQL round/pickup/work
commit. Matching work runs independently after ACK. Outbox relay uses
`ConfirmedPublisher` with persistent mandatory publishes and confirmation
tracking. A routed management-HTTP publish is insufficient confirm evidence.

RabbitMQ documents [confirms and manual ACKs](https://www.rabbitmq.com/docs/confirms)
as different boundaries; unacked deliveries are requeued on connection loss.
The [.NET guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide)
does not promise delivery of publications made during connection recovery.
The [reliability guide](https://www.rabbitmq.com/docs/reliability) requires
retransmission of unconfirmed work with duplicate tolerance. These sources
were checked on 2026-10-30. Recovery proves at-least-once convergence.

Redis reconstruction is explicit event replay, not automatic self-healing or
a new Driver state synchronization service. The existing atomic Lua update
and durable Driver authority remain the contract.

## Run and reset

Prerequisites: Docker Compose, Python 3.11+, .NET 10 SDK (`dotnet` on PATH,
or the W13 helper's existing user-SDK fallback). No Python packages are needed.

```sh
python scripts/perf/w14.py g4
python -m unittest discover -s scripts/perf/tests -v
python scripts/perf/w14.py e04 --mode smoke --rate 0.5 --outage 8 --baseline 10
python scripts/perf/w14.py analyze evidence/member-b/w14-recovery/outputs/W14-E04-...
# Only with a genuinely certified G4 and all experiment prerequisites:
python scripts/perf/w14.py e04 --mode report --repeats 3
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests -c Release --filter 'FullyQualifiedName~BacklogRecoveryTests|FullyQualifiedName~RedisOutageRecoveryTests'
```

The runner resets **only** the fixed `ride-hailing-w14` project before each
repeat and removes its containers, networks and volumes in `finally`. It
layers `compose.w14.yaml` on the unchanged W13 resource freeze and maps ports
to 25432 (PostgreSQL), 26379 (Redis), 25673 (AMQP), 35672 (management), 28102
(Driver). `W14_<SERVICE>_PORT` can select explicit ports before execution.
Busy ports cause failure; the runner never increments ports or kills their
owners. The producer probe is tracked as a child process and closed on exit.
Never pass this override to the normal development project.

Output is ignored under `evidence/member-b/w14-recovery/outputs/`. A completed
run contains `manifest.json`, hashed confirm receipts, a queue/durable-work
time series, and identity-complete authoritative state. Retain these minimum
raw data with any accepted academic result. Compact curated evidence can
copy these files into an evidence bundle without discarding the raw inputs.
Schema: [w14-manifest.schema.json](schema/w14-manifest.schema.json).
Failed runs retain their incomplete manifest and cannot be analyzed as PASS.

The dataset has one real OFFLINE Driver and no available Driver: real Matching intake and
autonomous S0/S1/S2 exhaustion commit real `NO_DRIVER` and one
`MatchingFailedV1` Outbox event per identity. Driver authority must stay unchanged.
This is a bounded B application experiment, not a full Trip/Saga workload.
Separate real-provider tests exercise successful Driver reservation,
commit-before-ACK redelivery, process restart, broker stop/return, and stale
AVAILABLE projections rejected by Driver authority.

## Measurement and acceptance

The producer continues throughout interruption and recovery. Configured
rate is scheduling input only. Observed λ is the number of successful
confirmed publications whose send timestamp falls within the observed
interval, divided by that interval's measured duration. Recovery λ is
reported separately from outage λ. Publisher confirms come from the real
application `ConfirmedPublisher`; routed HTTP responses are never used.

Each sample records ready, unacked, consumer count, broker redelivery and
ACK counters, persisted intake, terminal work, pending work, and committed/
published Matching Outbox counts. Durable counts use one PostgreSQL statement
snapshot. Outage resource collection runs concurrently so Docker statistics
latency cannot prolong the controlled interruption. Actual T runs from
observed consumer removal to restoration request; restoration/startup delay
is reported separately through recovery observations. Analysis rejects
an actual T differing from configured T by more than 1 second or 10%, whichever
is greater, and requires the configured minimum healthy baseline interval.
Outstanding logical work is confirmed
identities minus terminal identities; it does not add unacked work to DB
obligations, which could double-count the same message.

μ is the terminal-count delta over the sampled recovery window, with every
interval's effective rate and its range retained. `B ≈ λT` is compared with
the observed queue and outstanding-work peaks. A drain prediction is absent
when μ ≤ observed recovery λ. The approximation includes sampled startup
effects and does not assume a constant μ. Queue zero, return to baseline DB
work under continued arrivals, full application quiescence, and confirmed
Outbox publication are separate barriers. Stable state is three consecutive
queue-zero observations with pending DB work no greater than the observed
pre-interruption baseline; the producer then stops and **all** identities
must finish and publish before final proof. Polling limits timestamp precision.

The B12 auditor runs read-only against the **actual experiment databases**.
AUD-01/02/06/08 must pass; A/C-only legs remain NOT_RUN. Separately rerun the
B12 auditor test suite for its positive/negative controls. A separate test
database auditor pass cannot stand in for this run-dataset audit.

Analysis rejects missing/tampered artifacts, uncertain source, nonmonotonic
or invalid timestamps, absent confirms, missing final identities, false
terminal outcomes, duplicate effects, missing repetitions or mixed groups.
It reports per-run samples, P50/P95 confirmation latency, and across-run
median/range/sample variance. Synthetic fixtures are always SYNTHETIC_ONLY;
real functional runs are SMOKE_ONLY; report mode additionally rechecks live
G4 and unchanged measured source. Exit 0 is valid smoke/synthetic/accepted
analysis, 2 is invalid/failure, and 3 is report-gate refusal.

## C quorum integration contract

No executable C-owned three-node `ha` profile exists at the inspected
`d300849` branch or `86062af` main. REL-16 team acceptance is
**SHARED_DEPENDENCY**. `w14_ha.py` is an observation/verification adapter,
not a cluster installer or a claim that REL-16 ran.

```sh
# C operates its own isolated experiment, preserving the B manifest contract.
python scripts/perf/w14_ha.py snapshot --management-port 35672 --out before.json
# C removes exactly one queue member and records the actual loss window.
python scripts/perf/w14_ha.py snapshot --management-port 35672 --out during.json
# C restores/rejoins the node.
python scripts/perf/w14_ha.py snapshot --management-port 35672 --out after.json
python scripts/perf/w14_ha.py verify --before before.json --during during.json --after after.json --lost-node rabbit@node1 --runs /path/to/source-bound-B-run
```

Keep snapshots in the ignored outputs directory (or outside the repository)
so capture itself does not dirty source provenance. C's application artifact
uses `experiment: REL16` and `runId: W14-REL16-...` in the shared schema,
with consumers left running and observed ACK progress during the loss window.
E04 artifacts are refused even if presented with a three-member snapshot.

The adapter checks three actual quorum members, all online at baseline, known leader, surviving
majority, restored membership/consumers, source equality and overlap with
the B observation window. B proof must use the same observed cluster, queue
member and broker node identities; mutable cluster name alone is insufficient.
Source must match all three snapshots, and both confirmed publications and
consumer ACKs must progress inside the node-loss window.
The single-node E04 runner cannot supply HA evidence for it. C must supply
the HA environment and orchestrated B application run; B can then execute
the adapter and existing provider tests against the agreed isolated service
endpoints. No production broker management is added. The adapter returns
B_SUPPORT_PASS only for valid real B evidence, never a team G5 PASS.
