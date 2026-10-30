# Member C fault and HA harnesses

- `ha-up.ps1 -Applications` forms the disposable three-node RabbitMQ 4.3.6
  cluster, starts Payment/Notification, and requests quorum membership growth.
- `rel15-broker-interruption.ps1 -Execute` restarts the primary node and checks
  Payment recovery through the configured endpoint list.
- `rel16-one-node-loss.ps1 -Node rabbitmq-3 -Execute` refuses to proceed when
  the target is quorum-critical, stops one node, verifies a critical quorum
  queue remains available, then restores the node in `finally`.

These scripts mutate only the explicitly selected Compose project. Use an
isolated `COMPOSE_PROJECT_NAME`; never point them at production.

Core must be healthy first. No fake config committed. Owner: C (OTel/platform), B (perf methodology). See Docs 02-03.
