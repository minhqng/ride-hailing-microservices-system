# Member C fault and HA harnesses

- `ha-up.ps1 -Applications` forms the disposable three-node RabbitMQ 4.3.6
  cluster, starts Payment/Notification/Prometheus, requests quorum membership
  growth, and fails unless every declared quorum queue reaches three online
  members.
- `rel15-broker-interruption.ps1 -Execute` stops Payment and the primary node,
  publishes an exact-duplicate probe batch through a surviving node, requires
  the queue backlog to become visible, then restores both boundaries. PASS
  requires fresh Payment connections, a drained queue, exactly one operation,
  provider effect, dedup marker and confirmed result Outbox row per unique
  command, and no `FAILED_VISIBLE` result.
- `rel16-one-node-loss.ps1 -Node rabbitmq-3 -Execute` refuses to proceed when
  the target is quorum-critical, stops one node, requires exactly 2/3 online
  members of `payment.commands.q` during the outage, runs the same duplicate
  workload while that node is down, verifies the consumer drains and the
  Payment/Outbox state converges exactly, then restores the node and waits for
  all 3/3 members to rejoin before reporting PASS.

REL-15/16 read the management API using `RABBITMQ_USER` / `RABBITMQ_PASSWORD`
from the environment when supplied (local defaults match `compose.yaml`).
These scripts mutate only the explicitly selected Compose project. Use an
isolated `COMPOSE_PROJECT_NAME`; never point them at production. Core must be
healthy first. See Docs 02–03 and 05C §30.
