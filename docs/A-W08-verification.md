# A-W7/W8 verification

Latest follow-up: [W7–W8 hardening review](A-W07-W08-hardening-review.md),
source `9f892db`: 268 unit and 126 integration tests passed. The original
verification below is retained as historical evidence for `04368d8`.

Recorded 2026-10-16 (Asia/Saigon). Runtime/test source anchor: `04368d8`.
README update: `65d22c9`. Subsequent evidence-only commits do not change runtime code.
Base: `ed14b19` on `MemC-W8-Distributed`, with A-W7 `1dffb26`.

## Observed results

| Check | Result |
|---|---|
| Trip Unit Release build | zero warnings, zero errors |
| Trip Integration Release build | zero warnings, zero errors |
| Complete Trip unit suite | 260 passed / 260, zero skipped |
| Complete Trip integration suite | 117 passed / 117, zero skipped |
| Git whitespace diff check | passed |

Integration uses actual PostgreSQL 18.6 and RabbitMQ 4.3.6-management in
disposable Testcontainers. The nine W8 cases cover:

- actual HTTP CreateRide through opt-in Trip runtime to real RabbitMQ and a
  durable Matching-style PostgreSQL sink; stable message ID/content and trace;
- broker stop/restart, readiness, saved-trip 503 and same-key HTTP recovery;
- duplicate delivery, fresh-message-ID business replay and stale round;
- actual consumer connection close after Trip commit but before ACK, followed
  by redelivery with one domain effect and one receipt;
- injected pre-commit failure rolling back both receipt and Trip;
- concurrent identical message claims, plus changed-content identity rejection;
- missing reservation round retained for review, Payment lineage retained pending;
- late reservation and Payment authorization after cancellation;
- stale/current Matching failure and EF model/migration consistency.

W7's cancel-first/reserve-first, lost-response, concurrent writers, migration
backfill and exact old-release/new-reservation cases are included in the same
117-test run. The full suite also retains earlier HTTP, persistence, lifecycle,
idempotency and assignment agreement coverage.

## Reproduce

Use a working Docker daemon. On this Windows host, Docker ran in WSL with a
temporary loopback relay; that machine-specific relay is not a repository dependency.

```bash
dotnet restore tests/Unit/RideHailing.TripService.UnitTests
dotnet restore tests/Integration/RideHailing.TripService.IntegrationTests
dotnet test tests/Unit/RideHailing.TripService.UnitTests -c Release --logger "trx;LogFileName=w7-w8-trip-unit.trx"
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests -c Release --logger "trx;LogFileName=w7-w8-trip-integration-final.trx"
```

The recorded runs used separately successful Release builds, followed by
`--no-build --no-restore`. TRX files remain in ignored project TestResults directories.

SHA256:

- unit: `7932F30DB075C901D86F00117BDFA169DF93570951306F40B171D905ECA80A91`
- integration: `6EFA72F539939ED920321C5770C9581C0598DD7D1F0942681DB19CF4A001E26B`

The initial integration run had 113 pass / 3 fail. Readiness JSON compatibility
and explicit test-client traceparent propagation were repaired; the subsequent
117-case run includes a new broker-restart case and passed in full.

## Practical limits

This verifies the A-owned implementation, not a production deployment or a
human peer approval. Matching-style sink and test authentication are explicitly
fixtures. B's missing matchingRound and released-identity contract need alignment.
Payment consumer is a durable skeleton awaiting Saga lineage. Automatic cleanup
dispatch, the DB/publish Outbox gap, complete authenticated dispatch and joint
B/C review remain later integration work. No full-repository CI result is claimed.
