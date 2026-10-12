# CON-01 evidence — run w06-contention-20261012-001

## Scope

`DriverReserveConcurrencyTests` full class (15 tests) on the code commit,
with the distinct-reservation contention theory (`N=2/10/50/100`,
Barrier-synchronized genuine overlap on real PostgreSQL) repeated 3 times.
Driver unit suite 72/72 and full driver integration suite 39/39 green on
the same tree.

## Code under test

Immutable code commit `7aa61da6561583e9450fa945b158aaa8a83a5a8e`
(`feat(driver): guard reservation with OCC single-winner contention
proof`). Verify identity:

```bash
git rev-parse 7aa61da^{tree}   # == manifest sourceState.tree
git show 7aa61da:tests/Integration/RideHailing.DriverService.IntegrationTests/DriverReserveConcurrencyTests.cs | sha256sum  # == manifest testSha256
```

## Reproduce

```bash
dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests -c Release \
  --filter "FullyQualifiedName~DriverReserveConcurrencyTests"
```

Requires Docker (disposable Testcontainers PostgreSQL 18.6, real provider).

## Result

15/15 PASS; all three contention repetitions report `won=1,
conflicts=N-1, errors=0` at every level (see `metrics/run-metrics.json`
and the `E01-FIRST-DATA` lines in `test-output.txt`). Same-reservation
N-way replay, stale-version fencing, cross-driver identity conflicts and
migration re-application are proven by their own cases in the class.
Timings are first E01 performance data only, not correctness criteria.
