# TEAM W12 / G4 closeout

G4 technical acceptance is verified on source `7be3441b48a0621acc97aad102fd242978a097bb`, tree `5ec550726fdd878a5c9c72cb947cd32dfb74c10f`, by [Linux CI 41432807889](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/41432807889). The synthetic checkout `89aef8af01f3dceb67ed1c6c4e4f41536363f5d7` has the identical tree and clean source status. Final PR [#58](https://github.com/minhqng/ride-hailing-microservices-system/pull/58) still requires its evidence-only final-tree CI and post-merge verification before merge completion can be reported.

Sources are A [#43](https://github.com/minhqng/ride-hailing-microservices-system/pull/43), B [#45](https://github.com/minhqng/ride-hailing-microservices-system/pull/45), and C [#51](https://github.com/minhqng/ride-hailing-microservices-system/pull/51), applied as 23 original-author weekly commits onto verified W11 main. Integrator correction `7be3441` supplies shared authentication, exact Matching-cleanup reconciliation, durable worker trace continuity and two cause-proven fixture/oracle repairs. These corrections and team reruns are not attributed as member-authored course artifacts. Original member evidence remains historical, including its earlier G4 BLOCKED verdict.

See [manifest](manifest.json), [canonical tasks](task-status.md), [gate coverage](gate-coverage.md), [test output](test-output.txt), and [authority results](final-state/core-gate.json).

This is a historical code-evidence snapshot created before the final candidate run. The current runtime script additionally executes live REL-09 terminal authorization decline through the real simulator API, broker outcome and exact Driver release. That extension passed locally and must pass final-tree CI. Final acceptance, merge SHA and post-merge CI are recorded on PR #58 and in the campaign report; the snapshot does not imply those later operations already occurred.

## Actual proof boundary

The runtime gate uses authenticated public Gateway registration/login and ride intake, autonomous durable Matching, real Driver HTTP reserve, real RabbitMQ commands, PostgreSQL authorities, Redis projection and Payment provider simulator effects. Two non-vacuous scenarios reach COMPLETED/CAPTURED and CANCELLED/COMPENSATED with exact release and void. Assignment snapshots independently compare Trip, Driver, Matching round and winning attempt. Final AUD-01..08 inspect the owning authorities; no local proxy is promoted to full authority proof.

Accept/start/complete are driven by the existing verification-only actor invoking production Trip handlers. Those operations have no public HTTP transport in the frozen route set; this evidence makes no public lifecycle-API claim. Payment is held before acceptance to create a deterministic cancellation window, then its real process resumes queued authorization and exact void. This is a controlled gate, not an arbitrary-crash recovery claim.

All five Gateway/Trip/Matching/Driver/Payment services appear in each exported scenario trace. Required application and RabbitMQ series exist with finite samples. The security gate runs 55 real-boundary checks and seven opt-in Identity tests. The standard regression job still reports one opt-in theory skip; the dedicated runtime job executes all seven cases with zero skips. The placeholder SystemTests project contains zero tests and is not used as functional-path evidence.

Human peer reviews, peer reruns and cross-explanations are explicitly waived by the project lead's instruction to skip human review. They are never represented as performed. Actual GitHub enforcement and all technical gates remain mandatory.

## Reproduce

Use a clean checkout with Docker, Python 3, OpenSSL and the .NET 10 SDK (the CI workflow supplies the SDK). Only use a fresh dedicated project; these commands remove that project's disposable volumes on cleanup.

```bash
export COMPOSE_PROJECT_NAME=team-w12-g4
export TRIP_MESSAGING_ENABLED=true
export B12_AUDIT_OUTPUT_DIR="$PWD/TestResults/b12-audit"
python3 scripts/security/init-local-auth.py
docker compose --profile obs up -d --build
python3 scripts/test/platform-smoke.py
python3 scripts/test/security-gate.py --identity-tests
dotnet build scripts/test/w10-g3-harness/RideHailing.W10Harness.csproj -c Release
python3 scripts/test/core-runtime-gate.py
dotnet test RideHailing.Distributed.slnx -c Release --logger trx --results-directory TestResults/regression
docker compose --profile obs down -v
```

The scripts fail on missing authority proof, denial/mutation mismatch, unresolved cleanup, missing/non-finite required metrics or missing cross-service traces. Raw local/CI outputs remain ignored or in linked workflow artifacts. Authentication setup and boundaries are documented in [the runtime contract](../../../docs/authentication-runtime.md).
