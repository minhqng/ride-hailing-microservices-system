# Matching Service — Owner B

- Owner: B (reviewer A, backup C)
- Authority: MatchingRound/Attempt, ranking, Redis projection use. Never writes Driver DB. Must never access `driver_db`.
- DB: `matching_db` (role `matching_app`)
- Deps: `ConnectionStrings__Postgres`, `Redis__ConnectionString`, `RabbitMq__Host`, `Services__DriverBaseUrl`
- Read: Docs 00 §8, 01 §8-9, 02 §12-13, 05B
- Bootstrap: skeleton only. NOT implemented: rounds, ranking, UNKNOWN reconciliation, Redis GEO logic.
