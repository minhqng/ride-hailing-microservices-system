# Member C final evidence manifest

Prepared: 2026-10-28

## Deterministic evidence available in the repository

- Payment operation FSM, replay/conflict and timeout-after-effect tests.
- Confirmed mandatory publisher and manual ACK consumer tests.
- Durable processed-message and Outbox relay tests, including REL-05/06/18
  deterministic seams.
- Environment-gated faultpoint tests.
- Notification non-authoritative duplicate projection tests.
- Versioned observability dashboards, release override, secret scanner and HA
  harness.

## Live evidence still required on a Docker-capable host

- REL-05/06/08 repeated real PostgreSQL/RabbitMQ runs.
- W12 dashboards populated with real traffic and trace propagation screenshot
  or exported query JSON.
- W13 3–5 raw metric snapshots per compared consumer replica condition.
- REL-15 endpoint-list reconnect, fresh Payment connections and payment queue
  drain.
- REL-16 one-node-loss (2/3 online) plus restore/rejoin (3/3) and quorum status.
- Clean-clone release Compose smoke.

No live PASS is claimed on this machine: Docker CLI/engine was unavailable on
2026-10-28. Use the scripts under `scripts/evidence` and `scripts/fault`, then
append immutable run directories rather than editing this limitation away.

## Report wording

> RabbitMQ transport was treated as at-least-once. Publisher confirms protected
> publication acceptance while manual consumer acknowledgements transferred
> responsibility only after local durable processing. Duplicate publication or
> redelivery remained possible and was handled by stable message identity,
> durable deduplication and domain idempotency.
