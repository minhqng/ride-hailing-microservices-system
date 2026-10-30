# C-W12 — observability and core-gate support

Delivered:

- low-cardinality delivery, ACK, redelivery, retry, dead-letter, dedup, Outbox
  and Payment metrics;
- durable non-authoritative Notification projection with unit coverage;
- Prometheus scraping for OTel and selective RabbitMQ detailed metrics;
- provisioned RabbitMQ, Outbox/consumer and Payment/Notification dashboards;
- post-commit metric semantics: rolled-back effects and receipt replays do not
  inflate business counters.

The local RabbitMQ profile enables `rabbitmq_prometheus` on port 15692. Its
single-node quorum declarations preserve queue type but do not prove HA.

Verification lanes:

```bash
dotnet test tests/Unit/RideHailing.PaymentService.UnitTests -c Release
dotnet test tests/Unit/RideHailing.NotificationWorker.UnitTests -c Release
dotnet test tests/Integration/RideHailing.Platform.IntegrationTests -c Release \
  --filter "FullyQualifiedName~MessagingMetricsTests"
docker compose config --quiet
```
