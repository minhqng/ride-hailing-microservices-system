# Payment Service

Payment is the authority for simulated authorization, capture and void
operations. It is a development simulator, not a real card-network
integration, but its operation ledger is durable and transactionally correct.

- Database: `payment_db` (role `payment_app`), PostgreSQL.
- Depends on: `ConnectionStrings__Postgres`, `RabbitMq__Host`.

## Operation authority

Every logical request is keyed by an exact `operationKey` chosen by Trip
(`{tripId}:authorize|capture|void:v1`). PostgreSQL enforces one row per key:

- An equivalent request replays the canonical result.
- A different type, trip, amount or currency for the same key returns `409`
  without overwriting the original.
- Changing simulator mode does not mint a new logical request.

The provider simulator commits in an independent transaction and pool inside
`payment_db`, so a provider effect can survive rollback of the Payment
transaction. Recovery resolves that existing effect by the same key instead of
guessing.

## Broker contracts

`payment.commands.q` consumes `AuthorizePaymentV1`, `CapturePaymentV1` and
`VoidPaymentAuthorizationV1` from the Trip Outbox, with the envelope
`operationKey` as the idempotency authority. The consumer ACKs only after the
durable local effect and receipt commit. An uncertain outcome stays `Unknown`
and is reconciled against the same operation; it is never retried blindly.

The reconciliation worker resolves pending/unknown operations under persisted
backoff with a bounded attempt count. Exhaustion becomes `RequiresReview`, not
a fabricated success or failure.

## Simulator surface (Development only)

```bash
curl -X POST http://localhost:8105/simulator/operations \
  -H 'Content-Type: application/json' \
  -d '{"operationKey":"trip-demo:authorize:v1","tripId":"11111111-1111-1111-1111-111111111111","type":"Authorize","amountMinor":12000,"currency":"VND","mode":"Success"}'
```

| Method | Route | Purpose |
|---|---|---|
| POST | `/simulator/operations` | Create or replay an operation |
| GET | `/simulator/operations/{id}` | Read the persisted operation |
| POST | `/simulator/operations/{id}/reconcile` | Resolve an unknown outcome |
| POST | `/simulator/operations/{id}/retry` | Retry pending work under backoff |

`TimeoutAfterEffect` returns HTTP 504 while the persisted operation reports
`Unknown`; reconcile against the provider ledger to resolve it. `TransientFailure`
returns HTTP 503 and invalid request values return HTTP 400 before persistence.
These routes are absent from the Production overlay.

## Configuration

| Key | Meaning |
|---|---|
| `Outbox:BatchSize`, `ClaimLeaseSeconds`, `PollIntervalMilliseconds`, `MaxAttempts`, `RetryDelaySeconds` | Shared relay tuning |
| `ConnectionStrings__Postgres` | Owned database |
| `RabbitMq__Host`, `RabbitMq__Username`, `RabbitMq__Password` | Broker connection |

Readiness requires the owned database. The service never auto-migrates in the
release overlay; migrations run externally before startup.

## Verification

- `tests/Unit/RideHailing.PaymentService.UnitTests` — request fingerprinting,
  operation semantics and message contracts.
- `tests/Integration/RideHailing.PaymentService.IntegrationTests` — real
  PostgreSQL: persistence, concurrency, provider-effect rollback, retries and
  reconciliation.
- `scripts/test/payment-smoke.py` — disposable Compose run with process kill,
  durable replay and same-key reconciliation.
