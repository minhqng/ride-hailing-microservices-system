# Member C W08 RabbitMQ backbone - evidence run w08-rabbitmq-20261016-001

## Curation note

Leader-curated by the W08 integration engineer from exact-tree CI run [38912031301](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/38912031301) artifacts. Not member-authored; code ownership is unchanged (see git history).

## Scope

Platform Integration (38) + PaymentService Integration (26) + PaymentService Unit (25) on CI run 38912031301.

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

89/89 member-suite cases PASS, 0 failed. CI-wide: 851 passed / 0 failed / 1 pre-existing NotExecuted skip.

## Key proofs

- RabbitBackboneTests.ConfirmDoesNotMeanConsumerAck: confirm succeeds with no consumer (C-W08-09)
- RabbitBackboneTests commit-before-ACK redelivery proof; poison/exhausted to deadletter/quarantine with delivery-limit 20 (C-W08-04/08)
- RabbitBackboneTests.ConnectionLossFailsPublication + topology redeclaration; trace TraceId equality producer->consumer (C-W08-07/10)
- Topology smoke (platform-smoke job): quorum queues + .dead queues + fan-out probe routes, restart snapshot identical (C-W08-08)
- Payment integration/unit suites green: TIMEOUT_AFTER_EFFECT + same-key recovery regression (C platform scope)

## Boundary

Transactional Outbox/Inbox relay (C-W09) and Payment capture/publication (C-W10-06/07) deferred; W08 producers are best-effort-after-commit.
