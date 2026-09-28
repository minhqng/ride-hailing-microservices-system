# Member C — Week 2 platform foundation

Doc 05C §21 and Doc 04 §13 govern this week. Core services are deliberately unprofiled so both `docker compose up` and `docker compose --profile core up` start the core platform. `obs` adds Collector, Jaeger, Prometheus and Grafana.

| Issue | Implementation / verification |
|---|---|
| C-W02-01 | Compose, service DNS, Dockerfiles; platform-smoke CI builds a clean checkout |
| C-W02-02 | Six owned DBs; PUBLIC CONNECT revoked; smoke tests all 30 cross-role combinations with successful own-DB controls |
| C-W02-03 | Versioned broker service/bootstrap directory; executable business topology belongs to W4 |
| C-W02-04/05 | Collector pipelines, ASP.NET traces/metrics, Jaeger, Prometheus, Grafana sources; smoke queries a Payment trace |
| C-W02-06 | `/health/live` is process liveness; `/health/ready` verifies configured owned PostgreSQL with a bounded timeout. It does not assert Redis/Rabbit business readiness; no consumers exist yet. Notification is a worker skeleton, with no HTTP probe |
| C-W02-07 | OTel service.name, service.instance.id (container hostname unless overridden), deployment.environment |
| C-W02-08 | Build/test, Compose validation, real platform smoke; weekly branches and stacked PR bases trigger CI |
| C-W02-09 | `scripts/dev/reset.sh` explicitly destroys only the selected Compose project's development volumes. No business seed is required in W2; DB init creates roles/databases on an empty volume |
| C-W02-10 | Automated clean checkout smoke is provided; an actual teammate rerun is pending |

Run the integration smoke on a disposable local project after `docker compose --profile obs up -d --build`: `python scripts/test/platform-smoke.py`. Do not treat a configured CI job as a passing run. CI run links and results are recorded in the PR.

Collector is optional: unavailable telemetry does not stop the application. Compose startup ordering is not runtime resilience. Worker business processing, Outbox, retries and detailed instrumentation remain later-week work. A/B review and team signoff must come from the team.
