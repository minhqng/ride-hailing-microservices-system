# TEAM W14 technical acceptance and release evidence

All 21 mandatory A/B/C technical tasks are verified. Optional A-W14-07 remains
DEFERRED_BY_CANONICAL_SCOPE. Human peer review/rerun is explicitly WAIVED by
the project lead; no human execution or GitHub approval is fabricated.
Final exact-tree CI, GitHub merge and fresh post-main CI are separate gates;
[TEAM PR #61](https://github.com/minhqng/ride-hailing-microservices-system/pull/61)
records their actual outcome and exact release SHA/tree.

Base: verified W13 main `9fe6012ce8d3ee80ad3c3a04cee1b3a51084e980`, tree
`47b18da9e4abe62621c16fcbd1fce5fca901878e`, fresh main CI41469700053.
Measurement/control source: `724d6c69aa9ee590c3aa4bb4afd1622ae6aa8719`, tree
`3799ca9a2ea5a19d03949c4d0c4644c81c14a168`. Subsequent closeout changes are
restricted to evidence. See [reproduction](reproduce.md), [G5 accounting](g5-summary.json),
[task matrix](member-progress.csv), [member summary](member-summary.csv) and
[artifact digests](artifact-manifest.json).

| Owner | Weekly source | Delta and credit |
|---|---|---|
| A | [#48](https://github.com/minhqng/ride-hailing-microservices-system/pull/48), `6dfd301e6939b7b37b839836aa87a58b2360a442` | two commits after b875d1c; actual authors retained |
| B | [#46](https://github.com/minhqng/ride-hailing-microservices-system/pull/46), `a86ceb550fa46fd0357c50f582e880763224d40b` | eight commits after16dc175; ninth,25c4327 certificate fix, already represented in W13 |
| C | [#53](https://github.com/minhqng/ride-hailing-microservices-system/pull/53), `c88ce66dc7faaa5c63703518ee91117e69b8d229` | three commits after9d57c4c; actual authors retained |

No cumulative W10–W13 or W15/W16 implementation was imported. Owner labels
follow canonical responsibility. Integrator fixes and executions are not
claimed as member-authored peer evidence. Original branches remain preserved.

## Critical recovery finding and resolution

A pre-fix full-cluster test left a cancelled Saga RELEASE_PENDING and Driver
RESERVED although its release intent was confirmed/PUBLISHED. No release
receipt or queued/dead-letter delivery remained. RabbitMQ reported changed
node UIDs and deletion of old quorum data: startup reran join_cluster on
persisted siblings. This violated work conservation after publication; it
does not prove that the relay marked an unconfirmed publication.

[Failed authorities](failure-evidence/team-broker-failed-state.json),
[reset observations](failure-evidence/broker-reset-observations.json), and
[earlier passing observations](pre-fix-observations/README.md) are preserved.
Earlier green cases did not override this counterexample. Correction18f0fc1
joins only at first initialization, persists/fences the seed, restores existing
membership on ordinary restart and refuses unknown existing data. It also
wires Driver commands/Outbox to C's endpoint list.

The corrected [conservation test](ha/corrected-conservation.json) obtains24
persistent AMQP confirmations before full broker shutdown, observes all24
undelivered messages after restoration, and verifies exactly12 Payment effects,
12 dedup receipts and12 confirmed result Outbox rows. Both siblings restore
membership without join_cluster or node UID replacement.

Other bounded fixes reconcile Trip endpoints and real Driver ownership,
initialize a shared cookie before server/CLI startup, redact secrets, preserve
cancellation queue validation and handle native exit codes/PowerShell arrays.
The quorum wrapper rejected two incomplete proof attempts: Payment restart
brought its broker dependency back, and final metadata queried the stopped
primary. Both failures are retained. Payment now restarts without dependencies;
version evidence comes from the actual surviving broker. The complete case passes.

## Live HA and owning-state proof

The [all-broker runtime](ha/corrected-broker-runtime.json) leaves all seven
application process/image identities unchanged. Two real outages hold new
authorization intents PENDING with null published_at. Restoring infrastructure
alone resumes completion and exact cancellation compensation. No manual
business redrive is claimed as automatic recovery.

The [primary-loss runtime](ha/final-primary-loss-runtime.json) SIGKILLs
rabbit@rabbitmq while it leads matching.commands.q. All19 critical quorum
queues have3 members and retain2 online members/live leaders throughout all3
journeys, returning to3/3 after restoration. Driver, Matching and Trip process
identities stay unchanged. The normal cancellation actor deliberately restarts
Payment without bringing its broker dependency back; this action is explicit.

Both cases retain actual Driver/Matching/Trip/Saga/Payment final rows, exact
winning reservation identities, authorization/capture/void or decline/release,
finite required metrics, correlated exported traces and AUD-01..08 PASS against
independent owning PostgreSQL authorities. The lifecycle actor invokes real
production handlers; no public accept/start/complete HTTP transport is claimed.
[Member A's metric observation](ha/member-a-recovery-metrics.json) additionally
observed pending0, oldest0, failed-visible0 and6 Saga series after recovery.

## E04 controlled backlog recovery

Three independent report-grade runs reset volumes, create one real OFFLINE
Driver with authenticated ownership, and drive confirmed AMQP input through
autonomous Matching NO_DRIVER work. Offered input is0.5/s; actual input is
measured and continues through recovery. Prefetch16, pool20 and resource
limits stay fixed. All7 app images are built once and reused with --no-build;
pre/post observations verify image identity and actual cgroup CPU/memory.
All3 runs have the same host/configuration/image fingerprint. Actual versions:
Docker Compose5.6.0, host SDK10.0.403, PostgreSQL18.6, RabbitMQ4.3.6, Redis8.10.2.

The complete [analysis](e04/analysis.json), confirmations, samples, manifests
and independent final states are retained. There are104 unique confirmed
intakes plus3 same-identity duplicates; all107 are ACKed. All104 unique rows
reach NO_DRIVER with exactly104 result Outbox effects, unchanged Driver
authority, no fabricated reservation and no duplicate business effect.

| Run | Unique confirms | Actual input/s | Outage s | Peak ready | Peak business work | Service/s | First success s | Steady s | Final quiescence s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 36 | 0.493 | 30.397 | 13 | 16 | 2.105 | 3.002 | 10.761 | 14.927 |
| 2 | 34 | 0.500 | 30.006 | 14 | 16 | 2.969 | 3.039 | 7.244 | 10.817 |
| 3 | 34 | 0.493 | 30.438 | 14 | 16 | 3.459 | 3.086 | 6.413 | 10.468 |

Median service rate2.969/s (range2.105–3.459); median steady recovery7.244s
(range6.413–10.761), sample variance5.327 s². Complete interval rates,
confirmation quantiles, model predictions and dispersion remain in the data.
Broker depth and application completion are separate barriers. Three consecutive
queue-zero/baseline-work samples precede a separate final quiescence check.
The drain formula is approximate: service rate varies and observations include
startup/reconnect and management polling delays.

This bounded queue-recovery result is not maximum sustainable capacity or
full ride throughput. A prior2/s diagnostic failed the strict steady barrier
with ongoing arrivals and is retained. The canonical20/s example is a planning
illustration, not a threshold. B's original0.5/s protocol is the controlled
study point. Pre-fix E04 remains historical; only the corrected-source group
is accepted for this closeout. Trip/Saga/Payment legs are outside B's no-fleet
experiment; separate full HA runtime supplies those positive proofs.

## G4, release and claim boundaries

[Current-source CI41486346643](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/41486346643)
passed all4 jobs. Downloaded archive digests and actual checkout
16e2ea80cb6522620e4295d1aca80cd3fa51c6fb/tree3799ca9... were verified:
1223 regression cases passed,0 failed;1 ordinary opt-in Identity theory skipped.
Seven actual Identity negative cases separately executed with0 skips. All55
HTTP security checks,3 real journeys, AUD-01..08, finite metrics and Payment
observations passed. [G4 artifacts](g4/) retain actual results. The fresh Linux
checkout built the full stack and executed the demo pathways; the clean
tracked local GitHub clone separately executed live3-node failure/recovery.

W13 E01/E02/E03 acceptance remains at its recorded source and scope; see
[W13 evidence](../w13-integration/README.md). Those measurements are not
relabeled as observations at the W14 release. No optional optimization,
PostgreSQL/Redis HA, whole-host HA, arbitrary partition tolerance, zero downtime
or general capacity/SLA claim is made. Redis reconstruction is explicit event
replay, distinct from survival/reconnect. Application hosts supervise RabbitMQ
reconnection with SDK automatic recovery disabled; Outbox retains retry authority.

Curated JSON normalizes BOM/whitespace to LF. Original core hashes are retained
alongside portable hashes. Raw local logs/TRX are not packed into the repo;
exact CI archives remain in GitHub with verified digests. Final exact-tree CI,
live protections, actual merge and fresh post-main CI remain separate mandatory
gates, recorded with their exact refs in TEAM PR61.
