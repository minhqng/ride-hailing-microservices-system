# Member C W11 fault platform evidence

Date: 2026-10-25

Implemented named, environment-gated barriers:

- `Consumer.AfterBusinessCommitBeforeAck`
- `Outbox.AfterPublishBeforeMark`
- `Payment.AfterProviderEffectBeforeResponse`
- `Driver.AfterReserveCommitBeforeResponse`
- `Saga.AfterPersistBeforeDispatch` contract name (the persisted Saga runtime is not present on this branch)

There is no HTTP/admin endpoint for fault activation. `RIDE_FAULTPOINTS` is read at process startup,
and every wait is cancellation/timeout bounded. A defect where `WaitForHitAsync` disposed its timeout
source too early was fixed and covered by `WaitingForMissingHitIsBounded`.

Commands run:

```text
dotnet build RideHailing.Distributed.slnx --no-restore

dotnet test tests/Unit/RideHailing.PaymentService.UnitTests/RideHailing.PaymentService.UnitTests.csproj --no-build --no-restore
30 passed, 0 failed.

dotnet test tests/Integration/RideHailing.Platform.IntegrationTests/RideHailing.Platform.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~OutboxRelayUnitTests|FullyQualifiedName~OutboxMessageTests|FullyQualifiedName~MessagingMetricsTests|FullyQualifiedName~FailurePolicyTests"
21 passed, 0 failed per run.

dotnet test tests/Unit/RideHailing.PaymentService.UnitTests/RideHailing.PaymentService.UnitTests.csproj --no-build --no-restore --filter FullyQualifiedName~FaultPointTests
3 passed, 0 failed per run.
```

Both filtered deterministic suites were run five consecutive times: 5/5 green for each suite.

The real PostgreSQL/RabbitMQ REL-05/06/08 repetitions remain externally blocked. Docker Desktop's
Start Menu/service registrations are stale, their target binaries are absent, and Testcontainers
fails with `Failed to connect to Docker endpoint at npipe://./pipe/docker_engine`. No result is
claimed for the container proofs or peer rerun.
