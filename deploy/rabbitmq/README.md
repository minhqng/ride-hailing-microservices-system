# RabbitMQ — local broker and declared topology

The broker runs in the core Compose stack. Topology is declared by Payment
Service on startup, so no Management UI setup is needed. Declarations are
idempotent and create durable topic exchanges `ride.commands` and `ride.events`,
plus consumer-specific durable quorum queues and bindings from `RideTopology`.

Management UI: <http://localhost:15672>. Local credentials are `ride_app` /
`ride_dev_pw` unless overridden in the ignored `.env` file. Development defaults
are placeholders only; set local-only values before exposing the broker beyond
the machine.

The versioned `enabled_plugins` file enables `rabbitmq_prometheus`; metrics are
available locally at `http://localhost:15692/metrics` and are scraped by the
`obs` profile. `rabbitmq.conf` fixes the 10 s statistics collection interval and
balanced quorum-queue leader placement.

## Single node vs three-node

The default stack runs **one** broker. Quorum queues are still declared there so
queue type matches the reliability design, but a single node demonstrates no
replication and no broker failover.

`deploy/compose/compose.ha.yaml` adds `rabbitmq-2` and `rabbitmq-3` with their own
volumes, and every service is given `RabbitMq__Hosts=rabbitmq,rabbitmq-2,rabbitmq-3`
so a client reconnects to a surviving node. `join-cluster.sh` and `ha-start.sh`
form the cluster; `health-ready.sh` waits for quorum membership. Prometheus
scrapes all three nodes via `prometheus-ha.yml`.

That overlay is a **single-host** quorum scenario. It proves the mechanism, not
multi-host or multi-region availability.

## Reliability policy

`reliability.json` is versioned and imported by `health-ready.sh` after normal
broker bootstrap. An existing broker receives the policy without queue deletion.
Business queues use confirmed at-least-once delivery with dead-lettering to the
durable quarantine once broker delivery failures exceed 20. Health requires a
successful import; do not remove the broker volume when upgrading.

Before changing queue arguments on an existing environment, drain or migrate
the affected queues explicitly; RabbitMQ does not allow incompatible durable
queue declarations in place.
