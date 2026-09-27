# Trip / Booking Service — Owner A

- Owner: A (reviewer B, backup C)
- Authority: ride lifecycle, FareSnapshot, assignment reference, Saga state, cancellation. Does NOT decide Driver reservability.
- DB: `trip_db` (role `trip_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3-4, 01 §6-7, 02 §14, 05A
- Bootstrap: skeleton only. NOT implemented: FSM, FareSnapshot, Outbox, Saga, compensation.
