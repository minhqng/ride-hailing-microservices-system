# Grafana - local dashboard UI

Start the optional `obs` profile with `docker compose --profile obs up -d`.
UI: <http://localhost:3000>.

Provisioning (`provisioning/`) wires two data sources - Prometheus (`uid:
prometheus`, default) and Jaeger (`uid: jaeger`) - and loads four immutable
dashboards from `provisioning/dashboards/json/`:

| Dashboard | Covers |
| --- | --- |
| RabbitMQ Delivery Reliability | queue depth, consumers, delivery/ACK rates, quarantine |
| Outbox and Consumer Reliability | outbox backlog, publish attempts, consumer lag |
| Payment and Notification | payment operation outcomes, notification projection |
| Trip, Saga and Outbox | trip lifecycle, Saga step/status, compensation |

Dashboards are provisioned read-only (`editable: false`, `disableDeletion:
true`) so a UI edit cannot silently diverge from the committed JSON. Change the
JSON and restart Grafana instead.

RabbitMQ queue panels read the selective `/metrics/detailed` scrape rather than
the full per-object metrics set; see [../prometheus/README.md](../prometheus/README.md).

Prometheus is reached inside the Compose network on `prometheus:9090`, so a host
port override (`OBS_PROMETHEUS_PORT`) does not affect Grafana.
