# Payment Simulator Service — Owner C

- Owner: C (reviewer A, backup B)
- Authority: Authorize/Capture/Void logical ops, idempotency by `operationKey`. No real provider.
- DB: `payment_db` (role `payment_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3, 01 §10, 02 §15, 05C
- Bootstrap: skeleton only. NOT implemented: idempotency, simulator modes, DLQ behavior.
