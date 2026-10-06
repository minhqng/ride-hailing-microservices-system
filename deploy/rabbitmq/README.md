# RabbitMQ — local broker and declared topology

The broker runs in the core Compose stack. Member C's W4 topology is declared by Payment Service on startup, so no Management UI setup is needed. Declarations are idempotent and create durable topic exchanges `ride.commands` and `ride.events`, plus consumer-specific durable quorum queues/bindings from `RideTopology`.

Management UI: http://localhost:15672. Local credentials are `ride_app` / `ride_dev_pw` unless overridden in the ignored `.env` file. Development defaults are placeholders only; set local-only values before exposing the broker beyond the machine.

The one-node local profile uses quorum queue declarations to keep queue type consistent with the reliability design. It does not demonstrate replication or broker HA; that belongs to the later three-node validation profile.

W2 provides BrokerBootstrapV1 for the two exchanges. W4 RideTopology extends the same declarations with queues and bindings; platform-smoke verifies exchange semantics across restart.


Reliability policy is versioned in `reliability.json` and imported by `health-ready.sh` after normal broker bootstrap. An existing broker receives the policy without queue deletion. Business queues use confirmed at-least-once dead-lettering to the durable quarantine when broker delivery failures exceed 20. See [runtime and upgrade instructions](../../docs/messaging-runtime.md#broker-delivery-limit-and-quarantine). Health requires successful import; do not remove the broker volume when upgrading.
