# C-W14 — RabbitMQ reconnect and quorum recovery

Delivered:

- three-node `ha` Compose overlay with durable RabbitMQ node identities;
- critical business queues remain quorum queues;
- endpoint-list-aware connection factory used by Payment, Notification,
  Driver publishing and Matching messaging;
- REL-15 broker interruption and REL-16 one-node-loss orchestration scripts;
- Prometheus HA scrape configuration and backlog/drain observation support.

Validation:

```powershell
./scripts/fault/ha-up.ps1 -Applications
./scripts/fault/rel15-broker-interruption.ps1 -Execute
./scripts/fault/rel16-one-node-loss.ps1 -Node rabbitmq-3 -Execute
```

The scripts preserve volumes and restore the stopped node/service. A successful
run requires a real duplicate workload, visible backlog for REL-15, continued
processing at 2/3 quorum for REL-16, exact Payment/provider/dedup counts, every
result Outbox row marked `PUBLISHED`, and a drained queue. It supports only the
recorded broker/application-boundary scenario; it does not prove arbitrary
partitions or whole-system availability.
