# Notification Worker — Owner C

- Owner: C (reviewer A, backup B)
- Authority: none (non-authoritative delivery projection). Must NOT mutate Trip/Driver/Payment truth.
- DB: `notification_db` (role `notification_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Hosts` (or `RabbitMq__Host`)
- Read: Docs 00 §3, 01 §11, 05C
- Runtime: consumes `notification.business-events.q` with manual ACK only
  after a durable `notification_deliveries` insert. The primary key
  `(consumer_scope, message_id)` makes redelivery a no-op.
- Failure is non-authoritative: retry/DLQ visibility never mutates
  Trip/Driver/Payment truth.
