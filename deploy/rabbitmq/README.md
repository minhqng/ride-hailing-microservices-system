# RabbitMQ W2 baseline

Payment startup invokes BrokerBootstrapV1 with a bounded timeout and configured credentials. It declares durable topic exchanges ride.commands and ride.events without auto-delete. Repeating startup preserves declarations. W4 adds consumer queues and catalog bindings; confirms/manual ACK belong to W8.

Use a disposable Compose project for smoke tests. Management UI: http://localhost:15672. Credentials come from local configuration; defaults are development placeholders.
