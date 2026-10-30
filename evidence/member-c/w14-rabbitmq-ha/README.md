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
./scripts/fault/ha-up.ps1
./scripts/fault/rel16-one-node-loss.ps1
./scripts/fault/rel15-broker-interruption.ps1
```

The scripts preserve volumes and restore the stopped node/service. A successful
run supports the bounded statement that confirmed publishing, manual ACK and
application backlog recovery survived the recorded scenario. It does not prove
arbitrary partitions or whole-system availability.
