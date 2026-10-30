# Grafana — local dashboard UI

Start the optional `obs` profile with `docker compose --profile obs up -d`. UI: http://localhost:3000.

Provisioning includes Prometheus and Jaeger data sources plus three immutable
Member C dashboards: RabbitMQ delivery reliability, Outbox/consumer reliability,
and Payment/Notification. RabbitMQ queue panels use the selective
`/metrics/detailed` scrape to avoid the full per-object metrics cost.
