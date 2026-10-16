# Member B W08 Matching/Driver broker integration - evidence run w08-rabbit-integration-20261016-001

## Curation note

Leader-curated by the W08 integration engineer from exact-tree CI run [38912031301](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/38912031301) artifacts. Not member-authored; code ownership is unchanged (see git history).

## Scope

MatchingService Integration (111) + MatchingService Unit (98) + DriverService Integration (56) + DriverService Unit (104) on CI run 38912031301.

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

369/369 member-suite cases PASS, 0 failed. CI-wide: 851 passed / 0 failed / 1 pre-existing NotExecuted skip.

## Key proofs

- BrokerConsumeTests.Commit_Before_ACK_Crash_Redelivers_And_Converges: own-message redelivery + one PROCESSING round + zero attempts (REL-06 closure, B-W08-02/07)
- BrokerConsumeTests.Duplicate_Deliveries_Converge_To_One_Round incl. 2-consumer race (CON-06, B-W08-07)
- RideRequestedEnvelopeIdentityTests: opaque string MessageId verbatim incl. non-UUID; first-writer wins; cross-trip conflict fail-loud (B-W08-01/02)
- MatchingInputMessageIdMigrationTests: fresh TEXT + W07 uuid::text upgrade preserve + idempotent re-apply (B-W08-02)
- DriverEventPublishTests: Available/Offline/Reserved/Released/LocationAccepted best-effort-after-commit with deterministic per-effect identity incl. replay same-id (B-W08-05)
- DriverProjectionHandler matrix: routing-key dispatch, version fencing, UNKNOWN no-advance preserved (B-W08-03/04)
- AtomicDispatchFenceTests + ReserveWorkflowTests W07 fence suites green (regression)

## Boundary

MatchingFailedV1 atomic publication is W09 (B-W09-03); DriverTripStarted/Completed transitions+publication are W10 (B-W10-01/02/03).
