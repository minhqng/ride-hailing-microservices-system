# CON-03 evidence — run w06-payment-20261012-001

## Scope

`PaymentConcurrencyTests.SynchronizedCallersConvergeToOneIndependentProviderEffect`
— Authorize/Capture/Void × N=10/50/100 (9 cases), plus the full payment
integration suite (20/20) on the same tree.

## Code under test

Immutable code commit `6ec803d3eb05c3ccd58078e8fda91ddda3107ff2`
(`feat(payment): prove concurrent same-key single effect with independent
provider ledger`). Verify identity:

```bash
git rev-parse 6ec803d^{tree}   # == manifest sourceState.tree
git show 6ec803d:tests/Integration/RideHailing.PaymentService.IntegrationTests/PaymentConcurrencyTests.cs | sha256sum  # == manifest testSha256
```

## Reproduce

```bash
dotnet test tests/Integration/RideHailing.PaymentService.IntegrationTests -c Release \
  --filter "FullyQualifiedName~SynchronizedCallersConverge"
```

Requires Docker (disposable Testcontainers PostgreSQL 18.6, real provider).

## Result

9/9 CON-03 cases PASS; every case reports `rows=1; providerEffects=1` with
all N callers converging to the identical canonical result. Full suite
20/20 PASS (replay, upgrade, persistence, migration repeat-init included).
P50/P95 are diagnostic only (dominated by the 2 s `SlowSuccess` simulator
delay); see `metrics/run-metrics.json`. Per-case stdout lines are quoted in
`test-output.txt`.
