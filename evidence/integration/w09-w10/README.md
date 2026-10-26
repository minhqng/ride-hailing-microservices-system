# W09/W10 integration acceptance

Independent integration-lead verification of all Member A/B/C Week 09 and
Week 10 work, integrated into `main` without merge commits. This bundle is
the durable record of what was integrated, what was verified, what was
deliberately excluded (W11/W12), and which boundary gaps remain.

This is **not** a member signoff, a peer rerun, or CI evidence. No test was
weakened or skipped to obtain these results.

## Integrated revision

| Item | Value |
|---|---|
| Integration branch | `integration/w09-w10` |
| Base | `origin/main` = `ba5bb40615564ab022ddb0ff5e933bde93ee15a1` |
| Verified tip | `b6d57b6aa22bd38d8e8d0a42ac2bea3f5fa3dd06` |
| Tree | `5de1ff927e3deadf66ef89c998f4d43ee1b2b60c` |
| Commits ahead of base | 30 |
| Merge commits | 0 (rebase/squash only) |
| Source PRs | #35, #36, #37, #39, #40, #41 (W10-only part) |
| Superseded PR | #38 (duplicate B-W09 PR against `main`; #37 is the real base) |
| Excluded | W11 (#42), W12 (#43), all W13 performance work |

Full source mapping and command results are in `manifest.json` and
`test-output.txt`.

## Attribution

Git author metadata is preserved from the source branches:

- 28 commits by `minhqng <minhnq.181005@gmail.com>` (A-W09, A-W10, B-W09,
  B-W10, integration repairs/evidence).
- 2 commits by `vitquaybarking <leanhviet2005@gmail.com>` (C-W09
  `5130b0f`, C-W10-only `5b07085`).

The integration lead is the committer of the rebased commits because the
rebase was the integration action. No source author was rewritten to the
integrator. W11/W12 refs were not modified.

## Verification results

Environment: .NET SDK 10.0.401, Release, Linux 7.3.0, Docker 29.7.2-ce,
PostgreSQL 18.6, RabbitMQ 4.3.6-management, Redis 8.10.2.

| Lane | Result |
|---|---|
| Solution build | **0 errors, 9 warnings** (non-blocking; see `test-output.txt` §1) |
| Full solution tests | **1049 passed, 0 failed, 1 skipped** (`Identity` opt-in skip); `SystemTests` has no discoverable tests |
| Trip W09 outbox + W10 saga integration | **28/28** |
| Platform W9 dedup / relay / Rabbit | **19/19** |
| Payment W10 recovery / messaging / concurrency / replay | **20/20** |
| Payment message contracts | **3/3** |
| Trip saga unit transitions/intents | **27/27** |

The full-suite per-project counts and the exact commands are in
`test-output.txt`. Per-test names for the targeted lanes are in `g2/`.

## G2 — real-infrastructure defect-class proofs

G2 uses disposable real PostgreSQL and RabbitMQ instances (Testcontainers),
not mocks. The scenarios map to the definitions in
`docs/03-verification-experiments-demo.md`.

| G2 scenario | Real-infra proof |
|---|---|
| REL-04 crash before publish | `TripW09OutboxTests.CrashBeforePublish_LeavesDurablePendingRow_ThenRestartPublishesSameIdentity`; `W9OutboxRelayTests.RelayRestartReclaimsClaimedPendingRowWithoutSilentLoss` |
| REL-05 accepted-then-mark-lost | `TripW09OutboxTests.LostMarkAfterAcceptedPublish_RepublishesSameIdentity_ThenSettles`; `W9OutboxRelayTests.PublishAcceptedThenMarkLostRepublishesSameIdentityAndDedupKeepsOneEffect` |
| REL-06 commit-before-ack redelivery | `W9DedupTests.CommitBeforeAckRedeliveryFindsCommittedMarkerAndAppliesNoSecondEffect` |
| REL-11 poison / delivery-limit quarantine | `RabbitBackboneTests.PoisonOrExhaustedTransientMovesToConfirmedErrorQueue`; `RabbitBackboneTests.BrokerDeliveryLimitPreservesOriginalInQuarantine` |

## G3 — three-service ride on the real compose stack

The campaign ran against a freshly reset `docker compose` stack
(`postgres`, `rabbitmq`, `redis`, `identity`, `driver`, `matching`, `trip`,
`payment`, `gateway`, `notification`). Each scenario used a dedicated
driver; non-target drivers were retired/offline before the scenario so the
matching projection contained exactly one candidate.

The controlled actor (`scripts/test/w10-g3-harness`) is verification-only:
it calls the production Trip handlers (`CreateRide`, `RideRequestedIntake`,
`RecordDriverAccepted`, `StartTrip`, `CompleteTrip`) and the production
Matching reserve orchestrator. It writes no business rows directly and
emits no authority events. All cross-service work is performed by the real
services through the outbox, RabbitMQ, and the service databases.

| Scenario | Trip / saga | Payment | Driver | Result |
|---|---|---|---|---|
| Happy path | `COMPLETED` v6; `COMPLETED/CAPTURED` v4 | authorize + capture `SUCCEEDED`; 2 effects | `AVAILABLE` v5 | PASS |
| REL-07 restart while Payment result is queued | `READY_TO_START` v4; same saga `ACTIVE/AUTHORIZED` | one `AUTHORIZE`, effect 1 | `RESERVED` v5 | PASS |
| REL-08 lost response after effect, then reconcile | `DRIVER_ACCEPTED` v3; `AUTHORIZE_PENDING` | `UNKNOWN` → `SUCCEEDED`, effect count stays 1 | `RESERVED` | PASS |
| REL-09 terminal decline / compensation | `CANCELLED` v6; `COMPENSATED/RELEASE_PENDING` | `DECLINED`, 0 effects | `AVAILABLE` v6, unbound | PASS |
| REL-10 capture timeout before effect | `COMPLETED` v6; `CAPTURE_PENDING` | capture `UNKNOWN`, 0 capture effects | `AVAILABLE` v7 | PASS |

Scenario identifiers and the full database assertions are in `g3/`:

| Scenario | Trip | Driver | Reservation | Saga |
|---|---|---|---|---|
| Happy | `01b91744-02dd-4632-98f3-33fe6ee465a9` | `5cd8a80c-ff50-4040-8530-1937b046e5f8` | `2b5255ec-bb0b-4d22-ae68-f1cc1a5cdcb9` | `cf96caa8-f523-4d38-83df-9aa9828ee68f` |
| REL-07 | `5b1d9490-2902-4606-8ca9-bd26f8e42840` | `50aac10b-6aac-488c-8844-b811426a76a1` | `94dd26c5-5337-44df-9b2d-48c6cb4ac12c` | `d3a303e1-6347-49c4-a6a9-493babd91648` |
| REL-08 | `3d7aa399-add5-41c5-8e8e-d186a9d5fda6` | `ca87f6cd-022c-4078-a458-db8990cecdab` | `a4ea9ae2-e55e-4418-b5bd-58463f982a62` | `995808b1-23b0-46ce-b29f-ecc550b4de5f` |
| REL-09 | `8ee137a4-152f-491e-bf93-1db3188be88f` | `f1560c46-d212-4a4e-8f84-a8af0abb4b5a` | `18f6872a-17a2-48f7-a13e-07c9fa4ceb76` | `e271a507-3b8f-4776-9a98-45f91947a57c` |
| REL-10 | `1f947b40-019e-41d8-9e6a-90580850103f` | `557e82e9-cf4a-4957-8a0e-309e61f41fba` | `dee506a3-8bc9-4aae-bfce-ff9a2bb4fff0` | `f5aa9407-c299-458a-a837-511f17935e20` |

Key assertions:

- **Happy:** exactly one `RideRequestedV1`, `AuthorizePaymentV1`,
  `MarkDriverTripStartedV1`, `MarkDriverTripCompletedV1`, and
  `CapturePaymentV1` outbox row, all `PUBLISHED`; Payment has exactly two
  provider effects; Trip stays `COMPLETED`; driver returns to `AVAILABLE`.
- **REL-07:** Payment is stopped before accept, Trip is stopped while the
  Payment result is queued, both are restarted; the saga id is identical
  before and after restart (`d3a303e1…`), the authorize operation key is
  unchanged, and exactly one authorize effect exists.
- **REL-08:** the simulator pre-seeds `TimeoutAfterEffect`, so one provider
  effect exists while the Payment operation is `UNKNOWN`; reconcile under
  the same operation key turns it into `SUCCEEDED` with the effect count
  still 1. Trip remains `DRIVER_ACCEPTED/AUTHORIZE_PENDING` with
  `last_error_code=PROVIDER_RESPONSE_LOST` — see the boundary gap below.
- **REL-09:** payment declines without an effect; Trip cancels,
  compensates, releases the exact reservation, and emits exactly one
  `CancelMatchingRoundV1` and one `ReleaseDriverReservationV1`; no capture
  or void is emitted; driver is `AVAILABLE` and unbound.
- **REL-10:** capture is pre-seeded `TimeoutBeforeEffect`; Trip remains
  `COMPLETED`, the saga stays `CAPTURE_PENDING`, exactly one
  `CapturePaymentV1` exists, and the capture operation is `UNKNOWN` with
  no effect and no state regression.

`g3/runtime-events.txt` is a verbatim excerpt of the Trip, Payment, and
Matching container logs from this campaign. `g3/queues.json` is the broker
state after the campaign. `g3/compose-services.txt` records the running
services and their images.

## Boundary gaps (accepted and documented)

These are real W09/W10 boundaries. They are not hidden, not simulated away,
and not repaired by importing W11/W12:

1. **`CancelMatchingRoundV1` has no Matching consumer.** The REL-09
   compensation path emits the command and records the cleanup intent, but
   no consumer exists in the W09/W10 scope. The round remains `RESERVED`,
   `trip_cleanup_intents` key `matching:1` has `completed_at = null`, and
   `matching.commands.q` retains one ready message with no consumer.
2. **Payment reconcile does not republish a Payment outcome to Trip.** The
   Payment side correctly reconciles `UNKNOWN` to `SUCCEEDED` under the same
   key with one effect, but the Trip saga remains
   `AUTHORIZE_PENDING`/`DRIVER_ACCEPTED`. Automatic Trip resume after a
   late reconcile would require a follow-up owner change.
3. **The full browser/HTTP ride is not exercisable end-to-end.** Identity,
   Gateway, and Trip auth are still skeleton surfaces, and Driver
   accept/start/complete transport is missing. The G3 campaign therefore
   drives the production Trip handlers and Matching reserve orchestrator
   through the committed controlled-actor harness. No new transport or auth
   scope was invented for the evidence.
4. **`notification.business-events.q` has 43 unconsumed messages.** The
   notification consumer is outside the W09/W10 acceptance scope; these
   messages are unrelated to the ride outcomes verified here.
5. Rare receipt disposition `NeedsReview/TerminalState` is a safe
   terminal-state observation when a driver fact arrives after completion;
   the happy-path campaign waits for the driver receipts before advancing,
   so the captured happy path is `Applied`/`BusinessReplay`.

## Evidence map

| Path | Contents |
|---|---|
| `manifest.json` | Machine-readable provenance, source mapping, commands, environments, boundary gaps |
| `test-output.txt` | Curated build/full-suite/targeted output and commands |
| `g2/*.txt` | Per-test names and summary for the targeted real-infra lanes |
| `g3/happy.json` | Happy-path final-state assertion |
| `g3/rel07-before-trip-restart.json`, `g3/rel07.json` | Same saga before/after restart |
| `g3/rel08-before-reconcile.json`, `g3/rel08-after-reconcile.json` | One effect, `UNKNOWN` → `SUCCEEDED` under one key |
| `g3/rel09.json` | Decline, compensation, exact release, no void/capture |
| `g3/rel10.json` | Completed ride, capture pending, no regression |
| `g3/campaign.log` | Campaign transcript with ids and PASS lines |
| `g3/drivers.jsonl` | Dedicated driver ids used for isolation |
| `g3/queues.json` | Broker state after the campaign |
| `g3/compose-services.txt` | Service/image/uptime snapshot |
| `g3/runtime-events.txt` | Trip/Payment/Matching log excerpt |
| `g3/reproduce-g3.sh` | Campaign reproduction driver |
| `g3/g3lib.sh`, `g3/capture_scenario.sh` | Helpers used by the campaign |

## Reproduction

The exact commands are also recorded in `manifest.json`.

```bash
# 1. Reset the real stack (destroys only the named compose project volumes)
cd <repo>
export COMPOSE_PROJECT_NAME=rh-w09w10-g3
export TRIP_MESSAGING_ENABLED=true
docker compose -f compose.yaml down -v
docker compose -f compose.yaml up -d

# If Prometheus port 9090 is already in use, add the local override:
#   -f /path/to/compose-g3-override.yaml

# 2. Wait until 8101-8105 and 8080 /health/ready return 200, then build the
#    controlled actor (the repository's bin/obj directories are gitignored).
docker run --rm -v "$PWD":/src -w /src/scripts/test/w10-g3-harness \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet build -c Release

# 3. Run the campaign; output goes to a gitignored outputs/ directory.
cd evidence/integration/w09-w10/g3
./reproduce-g3.sh

# 4. Targeted tests from the verified tree
cd <repo>
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests \
  -c Release --no-build \
  --filter "FullyQualifiedName~TripW09OutboxTests|FullyQualifiedName~TripW10SagaTests"
dotnet test tests/Integration/RideHailing.Platform.IntegrationTests \
  -c Release --no-build \
  --filter "FullyQualifiedName~RabbitBackboneTests|FullyQualifiedName~W9DedupTests|FullyQualifiedName~W9OutboxRelayTests"
dotnet test tests/Integration/RideHailing.PaymentService.IntegrationTests \
  -c Release --no-build \
  --filter "FullyQualifiedName~PaymentConcurrencyTests|FullyQualifiedName~PaymentMessagingTransactionTests|FullyQualifiedName~PaymentRecoveryTests|FullyQualifiedName~PaymentReplayTests"
```

## Verdict

**PASS for W09/W10 integration, with the documented boundary gaps above.**
W11/W12 implementation, evidence, and performance work were not performed
or merged in this change.
