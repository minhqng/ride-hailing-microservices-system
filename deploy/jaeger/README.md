# Jaeger — local trace UI

Start the optional `obs` profile with `docker compose --profile obs up -d`.
UI: <http://localhost:16686>.

Jaeger receives traces from the OTel Collector over OTLP gRPC (`jaeger:4317`);
the collector is the only component that talks to it. Services never export to
Jaeger directly.

Traces carry cross-service correlation: a single ride produces spans from
`trip`, `matching` and `driver` under one `traceId`, plus the outbox publish and
consumer spans that connect them. `scripts/test/platform-smoke.py` queries
Jaeger to prove one real trace exists, and
`tests/Integration/RideHailing.MatchingService.IntegrationTests/ReserveTraceContinuityTests.cs`
asserts the parent/child chain across the HTTP reserve boundary.

Jaeger is a diagnostic tool only. It holds no recovery state: losing it does not
affect readiness, business processing or message delivery.
