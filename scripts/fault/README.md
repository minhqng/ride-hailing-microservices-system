# Member C fault and HA harnesses

- `ha-up.ps1 -Applications` forms the disposable three-node RabbitMQ 4.3.6
  cluster, starts Payment/Notification/Prometheus, requests quorum membership
  growth, and fails unless every declared quorum queue reaches three online
  members.
- `rel15-broker-interruption.ps1 -Execute` restarts the primary node, requires
  every `payment-service` management-API connection to be newly established
  after the restart, and requires `payment.commands.q` to return to its
  pre-fault ready/unacked depth with its consumer attached. A session that
  stayed on another node fails the harness instead of producing a false PASS.
- `rel16-one-node-loss.ps1 -Node rabbitmq-3 -Execute` refuses to proceed when
  the target is quorum-critical, stops one node, requires exactly 2/3 online
  members of `payment.commands.q` during the outage, then restores the node and
  waits for all 3/3 members to rejoin before reporting PASS.

REL-15 reads the management API using `RABBITMQ_USER` / `RABBITMQ_PASSWORD`
from the environment when supplied (local defaults match `compose.yaml`).
These scripts mutate only the explicitly selected Compose project. Use an
isolated `COMPOSE_PROJECT_NAME`; never point them at production. Core must be
healthy first. See Docs 02–03 and 05C §30.
