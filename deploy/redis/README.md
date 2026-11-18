# Redis — Matching-owned derived read model

Redis holds a **derived** GEO index of driver positions and status, owned by the
Matching service. It is not an authority.

- Driver PostgreSQL remains the only authority on who holds a reservation.
  Redis can nominate a candidate; the Driver service decides.
- A stale or wrong entry costs a rejected reserve attempt and a retry against
  the next candidate. It cannot produce a double assignment.
- The projection is rebuilt from Driver events, so losing the Redis volume is a
  performance event, not a correctness event.

Configuration lives in root `compose.yaml` (`redis` service, `Redis__ConnectionString`
on Matching). Projection behaviour and staleness handling are covered by
`tests/Integration/RideHailing.MatchingService.IntegrationTests`
(`DriverLocationIngressTests`, `DriverProjectionStalenessHandlerTests`).
