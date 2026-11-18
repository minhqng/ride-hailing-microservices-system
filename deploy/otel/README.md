# OpenTelemetry Collector

The optional `obs` profile runs an OTLP Collector that fans telemetry out to
Jaeger (traces) and Prometheus (metrics). `collector.yml` is the whole
configuration: OTLP receivers on 4317 (gRPC) / 4318 (HTTP), a batch processor,
an `otlp/jaeger` trace exporter and a `prometheus` metrics exporter on 8889.

Services export ASP.NET request traces and built-in server metrics with
`service.name`, `service.instance.id` and `deployment.environment`. Domain and
messaging spans are instrumented too - `outbox.publish`, `publish`, `consume`,
`driver.reserve`, `driver.release`, `matching.round.execute`,
`matching.attempt.reserve` - so one ride is traceable end to end across
processes.

Prometheus scrapes the collector at `otel-collector:8889` (job
`application-otel`), not each service individually. That keeps the scrape
surface to one target and means a service never needs to expose a metrics port.

Collector failure does not gate readiness or business processing. Telemetry is
evidence, never recovery state.

Run `docker compose --profile obs up -d --build`, call `/info`, then inspect
Jaeger on <http://localhost:16686>. CI runs `scripts/test/platform-smoke.py`
and queries Jaeger to prove one trace.

Reference: <https://opentelemetry.io/docs/collector/configuration/>
