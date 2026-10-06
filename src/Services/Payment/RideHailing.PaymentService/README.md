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

Choose `TimeoutAfterEffect` to receive HTTP 504 while the persisted operation reports `state: Unknown` and `providerEffectOccurred: false` until reconciled against the independently committed provider simulator ledger; inspect it with the returned `id` at `GET /simulator/operations/{id}`. Reposting the same operation key returns the same persisted result, including after abrupt process restart.

Verification and known limits: `docs/05C-member-c-payment-messaging-platform.md` §22, Doc 01 §10. TransientFailure returns HTTP 503; illegal request values return HTTP 400 before persistence.

## Recovering an uncertain result

All simulator routes require the Development environment. They are direct service controls for verification, not authenticated Mobile payment APIs.

```bash
curl -X POST http://localhost:8105/simulator/operations/OPERATION_ID/reconcile
curl -X POST http://localhost:8105/simulator/operations/OPERATION_ID/retry \
  -H 'Content-Type: application/json' -d '{"mode":"Success"}'
```

Replace `OPERATION_ID` with the returned UUID. Reconcile keeps the same key: an existing provider effect yields Succeeded; absence yields Pending. Unknown cannot be retried before reconciliation. Ordinary POST replay never retries. Explicit retry only executes Pending work after persisted backoff, with at most three attempts; exhaustion yields RequiresReview. Retry/reconcile return the current operation (HTTP 200 when found), so inspect `state`; missing IDs return 404.

The provider simulator uses an independent transaction and bounded pool in `payment_db`. A provider effect can survive rollback of the Payment transaction. Recovery resolves that existing effect even when the new simulator mode says Decline. It does not call a real financial provider. Automated Saga scheduling and authenticated client-facing orchestration belong to later integration.

## Verification

Run `python scripts/test/payment-smoke.py` only against a disposable Compose project: it creates operations, sends SIGKILL to Payment, restarts it, and proves durable replay plus same-key reconciliation. Real PostgreSQL tests include all request fingerprint conflicts, barrier-synchronized N=10/50/100 for Authorize/Capture/Void, independent provider commit followed by Payment rollback, and finite retries. CI publishes TRX results and platform logs.
