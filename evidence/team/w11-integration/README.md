# Team fault-campaign integration

Base main: `86062af028d01b86f07b1c902f040a41c41fe57f`, tree
`f992222eba61ebc6bdb73c95a65456a8201ad5d1`.

Sources: A PR #42 (`6047a7a`, `46ac68a`), B PR #44 (seven commits
`608426e` through `3ba3724`), C PR #41 (`d300849`, fault-only extraction).
Original author fields and source references are preserved. Already-landed W10
Payment processing, envelope operation-key validation and Trip reason-code
compatibility are retained from main. No future-week code is included.

Main CI recovery: run 40343480340 attempt 2 passed all four existing jobs on the
base SHA above. Downloaded artifact 13127690962 contains 11 TRX files: 1049
passed, 0 failed, one skipped Identity fixture, zero discovered SystemTests.
Provider/platform, Payment persistence/restart and Rabbit topology smoke passed.
This establishes the base, not acceptance of this new candidate.

The integrator adds environment guards, external shared signals, A/B dispatch
adapters and shared-provider tests at real PostgreSQL/RabbitMQ boundaries.
REL-05 asserts pending Outbox after accepted publication, stable replay identity
and one durable effect. REL-06 asserts committed receipt/effect before ACK and
deduplicated redelivery. REL-08 asserts independent provider commit survives
local rollback and replay cannot create a second effect. The latter is a
transaction-boundary fault test, not a process-kill claim.

Run `bash scripts/fault/team-fault-campaign.sh` for five controlled passes.
Its raw outputs are ignored and uploaded by the candidate's additional CI job.
The ordinary CI jobs remain mandatory. All five jobs passed in
[run 41417786227](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/41417786227)
on code head `873b20d`, tree `15dbdb08172aa65372cbc0ec123ef5452dfbe360`.
GitHub checkout `0fd352e` has exactly that tree. Downloaded full-suite TRX:
1092 passed, 0 failed, one Identity opt-in skip; SystemTests discovers zero tests.
The repeated campaign has 105 Trip/shared-platform/Payment tests with no failures
or skips, plus all 35 B scenario cells PASS across five controlled passes.

The [manifest](manifest.json), [execution summary](test-output.txt),
[task matrix](task-status.md) and [final-state assertion results](final-state/fault-results.json)
bind this evidence to that exact source. Final-state proof combines executed
assertions and B's observed identities; it does not represent assertion output
as raw database exports. Provider startup logs confirm PostgreSQL 18.6,
RabbitMQ 4.3.6 and Redis 8.10.2. Artifact hashes were independently checked.

This closeout changes only evidence files. It still requires fresh CI on the
final PR head and fresh main push CI after any merge. No merge is claimed here.

## Human-process exception

On 2026-11-02 the project lead explicitly instructed: "i mean skip human review".
Human review, peer reruns and cross-explanation signoffs for this campaign are
waived by that instruction. They are not represented as performed or approved.
Automated review and all correctness, provider, CI and enforced GitHub gates
remain required. Historical member bundles retain their original pending status.

## Recovery claim limits

The B W11 Matching restart proof explicitly invokes reconciliation after restart;
it does not establish unattended recovery. Redis proof covers degradation,
reconnection and explicit event replay, not an automatic full rebuild. Production
recovery work belongs to the next weekly delta and is not silently backported.
