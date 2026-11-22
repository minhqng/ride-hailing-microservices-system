# Notification Worker

Notification is a non-authoritative delivery projection. It never mutates Trip,
Driver or Payment truth; an outage delays delivery but cannot block a ride.

- Database: `notification_db` (role `notification_app`).
- Depends on: `ConnectionStrings__Postgres`, `RabbitMq__Hosts` (or
  `RabbitMq__Host`), `Auth__PublicKeyBase64` (shared JWT validation key).

## Delivery model

`notification.business-events.q` is consumed with manual ACK only after a
durable local transaction. Two ledgers protect delivery:

- `notification_deliveries` records the consumer scope and message id.
- `notification_source_messages` makes a reused `messageId` with a changed
  recipient or payload a poison event.

`NotificationRequestedV1` additionally inserts one recipient-addressed
`notifications` row. The business key is
`(source_message_id, recipient_user_id, notification_type)`, so a redelivery
cannot create a duplicate inbox entry. Recipient authority comes from the
owning service: Trip uses `Trip.RiderId`; Driver uses `Driver.UserId`. The
worker never equates `driverId` with `userId` and never queries another
service's database.

## HTTP surface

| Method | Route | Purpose |
|---|---|---|
| GET | `/notifications/me` | Read the caller's inbox |
| GET | `/notifications/{id}` | Read one owned notification |
| POST | `/notifications/{id}/read` | Mark one owned notification read |

Only the JWT `sub` is trusted; foreign ids are reported as not found.

## Failure behavior

Transient database failures remain retryable; permanently invalid events reach
the visible error path. Retry and DLQ visibility never touch ride truth. The
worker starts even when the broker is temporarily unavailable and reconnects
with bounded backoff.

## Verification

`tests/Integration/RideHailing.NotificationService.IntegrationTests` covers
delivery, recipient isolation, deduplication and payload safety against real
PostgreSQL and RabbitMQ. `release-smoke.py` Scenario C stops the worker during a
ride and proves that notifications are delivered once after restart.
