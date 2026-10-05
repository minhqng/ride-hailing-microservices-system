# Payment Simulator Service — Owner C

- Owner: C (reviewer A, backup B)
- Authority: Authorize/Capture/Void logical ops, idempotency by `operationKey`. No real provider.
- DB: `payment_db` (role `payment_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3, 01 §10, 02 §15, 05C
- W3: Payment operation aggregate, PostgreSQL persistence bootstrap, and deterministic development simulator are implemented. `operationKey` replay protection is still W5 work; the W3 simulator endpoint is development-only and is not safe for production use.

With the stack running, a deterministic success can be exercised with:

```bash
curl -X POST http://localhost:8105/simulator/operations \
  -H 'Content-Type: application/json' \
  -d '{"operationKey":"trip-demo:authorize:v1","tripId":"11111111-1111-1111-1111-111111111111","type":"Authorize","amountMinor":12000,"currency":"VND","mode":"Success"}'
```

Choose `TimeoutAfterEffect` to receive HTTP 504 while the persisted operation reports `state: Unknown` and `providerEffectOccurred: true`; inspect it with the returned `id` at `GET /simulator/operations/{id}`. The same operation key is not yet deduplicated in W3.

Verification and known limits: `docs/05C-member-c-payment-messaging-platform.md` §22, Doc 01 §10. TransientFailure returns HTTP 503; illegal request values return HTTP 400 before persistence.
