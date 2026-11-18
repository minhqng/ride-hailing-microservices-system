# Prometheus — local metrics UI

Start the optional `obs` profile with `docker compose --profile obs up -d`.
UI: <http://localhost:9090>.

The host port is configurable so the profile can start on a machine where 9090
is already taken:

```bash
OBS_PROMETHEUS_PORT=19090 docker compose --profile obs up -d
```

Inside the Compose network Prometheus always listens on 9090; only the published
host port changes. `scripts/test/platform-smoke.py` and
`scripts/test/core-runtime-gate.py` read the same `OBS_PROMETHEUS_PORT`
variable, so one setting governs both the stack and the gates that inspect it.

## Scrape targets

`prometheus.yml` (development) and `prometheus-ha.yml` (three-node broker
overlay) scrape four jobs:

| Job | Target | Purpose |
| --- | --- | --- |
| `prometheus` | `prometheus:9090` | self-scrape |
| `application-otel` | `otel-collector:8889` | all service traces/metrics via OTLP |
| `rabbitmq` | `rabbitmq:15692` | broker aggregate metrics |
| `rabbitmq-detailed` | `rabbitmq:15692/metrics/detailed` | queue/consumer/exchange families used by the dashboards |

The HA variant adds `rabbitmq-2` and `rabbitmq-3` to both broker jobs.

The selective `/metrics/detailed` scrape exists because the full per-object
RabbitMQ metrics set is expensive on a busy broker; the dashboards only need
queue, consumer, delivery and exchange families.

Verify targets are up:

```bash
curl -sS "http://127.0.0.1:${OBS_PROMETHEUS_PORT:-9090}/api/v1/targets?state=active"
```
