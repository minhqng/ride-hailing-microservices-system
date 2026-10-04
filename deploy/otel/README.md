# W2 OpenTelemetry baseline

The optional `obs` profile runs an OTLP Collector, Jaeger, Prometheus and provisioned Grafana data sources. Services export ASP.NET request traces and built-in server metrics with service.name, service.instance.id and deployment.environment. Collector failure does not gate readiness or business processing.

Run `docker compose --profile obs up -d --build`, call `/info`, then inspect Jaeger on localhost:16686. CI runs `scripts/test/platform-smoke.py` and queries Jaeger to prove one trace. Fine-grained domain spans and worker messaging spans belong to later weeks.

Reference: https://opentelemetry.io/docs/collector/configuration/
