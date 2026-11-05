# DS03 functional closure evidence (Driver Reject + recipient notifications)

Scope: the two remaining DS03 course statements — “Drivers accept or reject
trips” and “Send notifications to passengers and drivers” — implemented,
reviewed and verified on top of the W16-integrated `main`.

- Base main: `b03b30d1aec40053242381c27cecf36112866e03`
  (tree `9ec89714a75fa419bf8b14b03f91fd38e8dac201`)
- Verified source commit: `f2ff224f397c76d66af20358fe7615d6aba186ad`
  (tree `caac264b93c1d5b441ac1b1d0be87253162ccbc0`)
- This directory is an evidence-only commit. Everything outside `evidence/`
  is unchanged from the verified source commit.
- Release tag: **NOT CREATED** (no v1.0.0).
- Note (2026-11-05): main was rewritten for an attribution correction with an
  identical tree, and this DS03 stack was rebased from old main
  `2facf746...` onto `b03b30d1...`. The old main is preserved by the remote
  tag `archive/main-pre-attribution-correction-2026-11-05`.

## What was implemented

- **Driver Reject** (Doc 01 §40.1, Doc 05A §37): authenticated
  `POST /trips/{tripId}/reject`; exact current `(driverId, reservationId)`
  fencing; durable `MATCH_FAILED/failure_reason=DRIVER_REJECTED`; atomic
  `CancelMatchingRoundV1` + `ReleaseDriverReservationV1` Outbox intents;
  idempotent replay; no rematch; no payment; no false release claim. The
  authoritative Trip read surface exposes the reason. A Driver-authority
  reconciler completes a lost release only on conclusive not-owned proof and
  advances a compensating Saga as the event path would. No automatic rematch.
- **Recipient notifications** (Doc 01 §40.2, Doc 02 §43, Doc 05C §48):
  `NotificationRequestedV1` intents emitted by the Trip/Driver Outboxes;
  per-recipient inbox rows in `notification_db`; deterministic notification
  identity and a global source-message fingerprint ledger (a reused messageId
  with changed recipient/payload is poison); JWT owner-isolated
  `GET /notifications/me`, `GET /notifications/{id}` and
  `POST /notifications/{id}/read`; non-authoritative and non-blocking.

## Verification

- Full .NET regression on the verified source: **1,341 passed, 0 failed,
  1 known Identity opt-in skip across 13 executable suites** (baseline was
  1,260; +81 new tests). TRX verifier PASS.
- Production release overlay smoke on the exact verified source: **PASS**,
  19 checks, external EF migration to 11 applied migrations, real PostgreSQL /
  RabbitMQ / Gateway / JWTs, Scenarios A–D below, final authority audit and
  isolated teardown.
- Security: hygiene 76 changed paths clean and 24 self-test verdicts OK;
  release overlay fails closed without secrets; unauthenticated inbox access
  is 401, cross-user/foreign notification ids are 404, and a forged
  client-supplied `userId` is not accepted.
- Independent fresh-context reviews of the Reject and Notification change sets:
  their P1 findings were fixed and re-verified (Trip read truth, reconciler
  Saga completion, global source-message conflict ledger); no open P0/P1.

### Release-smoke scenarios

| Scenario | Result |
| --- | --- |
| A — authenticated Driver rejects; exact release completed; no payment; Rider can book again; Rider inbox records rejection | PASS (Trip `MATCH_FAILED`, `DRIVER_REJECTED`, release completed) |
| B — Driver accepts, payment authorizes, start/complete/capture; both roles read own inbox | PASS (4 recipient notifications for the completed trip) |
| C — Notification worker stopped during ride; Trip/Driver/Payment complete; restart delivers queued notifications exactly once | PASS (Trip `COMPLETED`, 4 notifications, no duplicates) |
| D — reject commit then Trip process crash/restart; Outbox/reconciliation resumes; exact replay | PASS (`replayed=true`, 1 release intent, 1 rejection notification) |

Final audit on the scenario database: 6 Trips (`COMPLETED`, `CANCELLED`,
`MATCH_FAILED`), 1 `AVAILABLE` Driver with no reservation, 6 historical
matching rounds, unique Payment operation keys with matching provider effects,
and 25 unique recipient notifications with no token/credential/payment-secret
content. All cleanup intents completed; teardown preserved an unrelated
sentinel container.

## What this bundle does not claim

- No GitHub PR merge, exact-tree CI run or post-merge main CI: `gh` is not
  authenticated in this environment. Branch commits are prepared locally.
- No automatic rematch, SMS, email, mobile push, Firebase or real provider
  notification channel.
- No new W13 performance measurement; earlier performance figures are not
  reused as new-source results.
- `MATCH_FAILED` rows created before this amendment may have
  `failure_reason = NULL`; they are never backfilled by guesswork.

See `test-output.txt`, `manifest.json`, `final-state/release-smoke-summary.json`
and `reproduce.md`.
