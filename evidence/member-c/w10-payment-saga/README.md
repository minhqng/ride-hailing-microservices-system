# Member C W10 Payment ↔ Saga boundary evidence

Date: 2026-10-25

## Implemented Payment side

- `payment.commands.q` consumes `AuthorizePaymentV1`, `VoidPaymentAuthorizationV1`, and `CapturePaymentV1` with manual ACK.
- The command's envelope `operationKey` must exactly match the payload key; the Trip identity must also match when present.
- One local `payment_db` transaction contains the processed-message marker, authoritative Payment state transition, and result Outbox intent.
- Result message identity is stable across redelivery and producer retries: `payment:<operation-id>:<event-type>`.
- The hosted worker runs the command consumer and shared Outbox relay; the relay publishes with confirms and marks only after acceptance.
- Result routes implemented: authorized, authorization-failed, authorization-voided, captured, capture-pending, and requires-review.
- Payment never writes the Trip database.

## Verification

```text
dotnet build RideHailing.Distributed.slnx --no-restore
Build succeeded (0 errors).

dotnet test tests/Unit/RideHailing.PaymentService.UnitTests/RideHailing.PaymentService.UnitTests.csproj --no-build --no-restore
30 passed, 0 failed.

dotnet test tests/Integration/RideHailing.Platform.IntegrationTests/RideHailing.Platform.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName!~W9DedupTests&FullyQualifiedName!~W9OutboxRelayTests&FullyQualifiedName!~RabbitBackboneTests&FullyQualifiedName!~DbIsolationTests"
37 passed, 0 failed.
```

`PaymentMessagingTransactionTests.RedeliveryKeepsOnePaymentEffectMarkerAndResultIntent`
is compiled and is the PostgreSQL proof for one operation, one provider effect,
one processed marker, and one result Outbox row after redelivery.

## External integration boundary

The repository does not yet contain Member A's persisted Saga/dispatch runtime, so REL-09, REL-10,
and the full happy-ride Saga consumer cannot be truthfully executed from this branch. Payment's
contract and durable result boundary are ready for that consumer.

The PostgreSQL/RabbitMQ test cannot run on this workstation until a working Docker engine is
available. Testcontainers reports `Failed to connect to Docker endpoint at npipe://./pipe/docker_engine`.
