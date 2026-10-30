# Member C W15 freeze manifest

Prepared: 2026-10-30

## Deterministic evidence available in the repository

- Payment operation FSM, replay/conflict, and timeout-after-effect tests.
- Confirmed mandatory publisher and manual ACK consumer tests.
- Durable processed-message and Outbox relay tests, including REL-05/06/18 seams.
- Environment-gated faultpoint tests.
- Notification non-authoritative duplicate projection tests.
- Versioned observability dashboards, release override, secret scanner, and HA harness.

## Validation boundary

The Release solution build, Member C unit/integration suites, PowerShell syntax,
secret scan, and release/HA Compose rendering were validated during the stacked
W12-W15 preparation. Compose was rendered through the WSL Docker CLI.

Real REL-15/16 node-loss and recovery output, measured W13 performance samples,
and three clean-clone release rehearsals still require preserved live evidence.
No runtime HA PASS is inferred from configuration validation.

## Report wording

> RabbitMQ transport was treated as at-least-once. Publisher confirms protected
> publication acceptance while manual consumer acknowledgements transferred
> responsibility only after local durable processing. Duplicate publication or
> redelivery remained possible and was handled by stable message identity,
> durable deduplication, and domain idempotency.
