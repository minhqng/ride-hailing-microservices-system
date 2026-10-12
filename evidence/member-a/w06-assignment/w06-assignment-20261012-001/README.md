# Assignment evidence — run w06-assignment-20261012-001

## Scope

`TripW06AssignmentProofsTests` (5 tests: competing-reservation single
winner, replay no-write, MatchingFailed non-regression, terminal fencing)
plus `TripDriverAgreementTests` (3 tests: winner/loser/replay agreement
against the real Driver authority). Full Trip regression (252/252 unit,
97/97 integration) green on the same tree.

## Code under test

Immutable code commit `183981ddce8cd2847c864366871e779e2d0df7e9`
(`feat(trip): enforce single-assignment replay fencing with Driver
agreement proof`). Verify identity:

```bash
git rev-parse 183981d^{tree}   # == manifest sourceState.tree
git show 183981d:tests/Integration/RideHailing.TripService.IntegrationTests/TripW06AssignmentProofsTests.cs | sha256sum  # == manifest proofTestSha256
git show 183981d:tests/Integration/RideHailing.TripService.IntegrationTests/TripDriverAgreementTests.cs | sha256sum  # == manifest agreementTestSha256
```

## Reproduce

```bash
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests -c Release \
  --filter "FullyQualifiedName~TripW06AssignmentProofsTests|FullyQualifiedName~TripDriverAgreementTests"
```

Requires Docker (disposable Testcontainers PostgreSQL 18.6, real provider;
the agreement fixture spins a real Driver database beside the Trip one).

## Result

8/8 PASS (listed in `test-output.txt`). Every test asserts the reloaded
authoritative row, never handler results alone. Contention metrics are N/A
with reason (deterministic two-writer proof; B-owned CON-01 separately
proven in `member-b/w06-contention` on the same tree).
