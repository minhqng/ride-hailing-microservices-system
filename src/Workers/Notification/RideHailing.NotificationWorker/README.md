# Notification Worker — Owner C

- Owner: C (reviewer A, backup B)
- Authority: none (non-authoritative delivery projection). Must NOT mutate Trip/Driver/Payment truth.
- DB: `notification_db` (role `notification_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Hosts` (or `RabbitMq__Host`),
  `Auth__PublicKeyBase64` (shared JWT validation key; required for the inbox API)
- Read: Docs 00 §3, 01 §40.2, 02 §43, 05C §48
- Runtime: consumes `notification.business-events.q` with manual ACK only
  after a durable local transaction.
  - Characterized business events keep the existing
    `notification_deliveries` receipt (`consumer_scope` + `message_id`).
  - `NotificationRequestedV1` additionally inserts one recipient-addressed
    `notifications` row. Receipts are per recipient; the global
    `notification_source_messages` ledger makes a reused messageId with a
    changed recipient/payload a poison event. The inbox business key is
    `(source_message_id, recipient_user_id, notification_type)`.
- Recipient authority: Trip uses `Trip.RiderId`; Driver uses `Driver.UserId`.
  The worker never queries another service's database and never equates
  `driverId` with `userId`.
- HTTP: `GET /notifications/me`, `GET /notifications/{id}`,
  `POST /notifications/{id}/read` (JWT `sub` only; foreign ids are not-found).
- Failure is non-authoritative: retry/DLQ visibility never mutates
  Trip/Driver/Payment truth. Transient notification DB failures remain
  retryable; permanently invalid events reach visible error handling.
