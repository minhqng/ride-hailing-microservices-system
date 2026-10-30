# Fault and HA harnesses

This directory contains two related lanes:

- deterministic faultpoints used by integration tests; and
- guarded Docker harnesses that exercise broker interruption and quorum loss.

The shared faultpoint platform is delivered by `RideHailing.Messaging.Faults`
(`FaultPoint`, `FaultPointNames`, environment gating). Member A's Trip-owned
gate is the only Trip seam on top of it:

- Name: `Saga.AfterPersistBeforeDispatch`
- Location: the Trip Outbox relay pre-publish seam
  (`OutboxRelayOptions.BeforePublish`) — necessarily after the local commit,
  before dispatch.
- Scope: the saga's outbound payment dispatches only.
- Activation: default-off config (`TripFaultPoints:*`) AND non-Production
  host environment; enabling in Production fails startup; no HTTP surface.
- Signaling: `reached` marker file + `release` file in a test-owned signal
  directory; bounded wait with a hard deadline.

Authoritative documentation: `src/Services/Trip/RideHailing.TripService/`
`Integration/FaultPoints/TripFaultPointGate.cs` and the Trip service README
(W11 section). Do not add a second faultpoint framework next to it.

## HA harnesses (Member C, W14+)

- `ha-up.ps1 -Applications` forms the disposable three-node RabbitMQ 4.3.6
  cluster, starts Payment/Notification, and requests quorum membership growth.
- `rel15-broker-interruption.ps1 -Execute` restarts the primary node, proves
  the application reconnected through the configured endpoint list, and
  requires the critical queue to drain to its pre-fault depth.
- `rel16-one-node-loss.ps1 -Node rabbitmq-3 -Execute` refuses to proceed when
  the target is quorum-critical, stops one node, verifies a critical quorum
  queue still has an online majority, then restores the node and waits for it
  to rejoin before reporting PASS.

These scripts mutate only the explicitly selected Compose project. Use an
isolated `COMPOSE_PROJECT_NAME`; never point them at production. Core must be
healthy first. See Docs 02–03 and 05C §30.
