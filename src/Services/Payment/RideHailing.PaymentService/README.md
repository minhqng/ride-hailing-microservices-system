# Payment Simulator Service — Owner C

- Owner: C (reviewer A, backup B)
- Authority: Authorize/Capture/Void logical ops, idempotency by `operationKey`. No real provider.
- DB: `payment_db` (role `payment_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3, 01 §10, 02 §15, 05C
- Development-only simulator backed by durable Payment operations. PostgreSQL enforces one exact `operationKey`; equivalent requests replay the canonical result, and changes to type, trip, amount or currency return HTTP 409 without overwriting the original. Changing simulator mode does not create a new logical request.

With the stack running, a deterministic success can be exercised with:

```bash
curl -X POST http://localhost:8105/simulator/operations \
  -H 'Content-Type: application/json' \
  -d '{"operationKey":"trip-demo:authorize:v1","tripId":"11111111-1111-1111-1111-111111111111","type":"Authorize","amountMinor":12000,"currency":"VND","mode":"Success"}'
```

Choose `TimeoutAfterEffect` to receive HTTP 504 while the persisted operation reports `state: Unknown` and `providerEffectOccurred: true`; inspect it with the returned `id` at `GET /simulator/operations/{id}`. Reposting the same operation key returns the same persisted result, including after abrupt process restart.

Verification and known limits: `docs/05C-member-c-payment-messaging-platform.md` §22, Doc 01 §10. TransientFailure returns HTTP 503; illegal request values return HTTP 400 before persistence.
