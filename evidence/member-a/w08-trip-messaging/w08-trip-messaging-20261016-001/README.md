# Member A W08 Trip messaging - evidence run w08-trip-messaging-20261016-001

## Curation note

Leader-curated by the W08 integration engineer from exact-tree CI run [38912031301](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/38912031301) artifacts. Not member-authored; code ownership is unchanged (see git history).

## Scope

TripService Integration (126) + TripService Unit (267) on CI run 38912031301.

## Code under test

Immutable code commit `708803fa68f428e6753fc430dab9af266ac926db` (tree `d68bf1f6acc80373444643c93723510c81c2c300`). Verify:

```bash
git rev-parse 708803fa68f428e6753fc430dab9af266ac926db^{tree}
```

## Reproduce

```bash
dotnet test tests/Integration/<Suite>.IntegrationTests -c Release
dotnet test tests/Unit/<Suite>.UnitTests -c Release
```
Requires Docker (disposable Testcontainers PostgreSQL 18.6 / RabbitMQ 4.3.6 / Redis 8.10.2, real providers).

## Result

393/393 member-suite cases PASS, 0 failed. CI-wide: 851 passed / 0 failed / 1 pre-existing NotExecuted skip.

## Key proofs

- TripW08MessagingTests: stable RideRequestedV1 publish bytes + MessageId (A-W08-01/04)
- TripW08MessagingTests: commit-before-ACK redelivery converges, no reapply (A-W08-04/05/09)
- TripW08MessagingTests: FailureBeforeCommit rolls back receipt+effect (A-W08-08)
- TripW08MessagingTests: concurrent same-id replay once; stale round fenced; terminal monotonicity (A-W08-02/05/08)
- TripW08MessagingTests: unmigrated-DB readiness gate 503, zero consumers, post-migrate drain (A-W08-10 freshness+upgrade)
- TripW08 migration 20261015161047_W08_TripMessageReceipts additive; W07 rows preserved (A-W08-10)

## Boundary

Payment skeleton parks PendingWorkflow/NeedsReview; full Outbox (A-W09) and Saga/capture (A-W10) deferred.
