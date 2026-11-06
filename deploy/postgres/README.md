# PostgreSQL roles and startup migrations

`deploy/postgres/init` creates the per-service databases and least-privilege
roles on a fresh volume. It does not run on an existing volume.

## Production

Trip owns EF migrations and applies them through the external
`dotnet ef database update` step documented in `docs/release.md`;
the release overlay sets `TripDatabase__AutoMigrate=false`.

Payment, Driver and Matching apply idempotent startup DDL inside a transaction
guarded by a stable PostgreSQL advisory key, so concurrent replicas serialize
the baseline and one migration execution is observed:

| Service | Advisory key | Coordination |
| --- | --- | --- |
| Payment | 754301 | `pg_advisory_xact_lock` around the embedded SQL migrations |
| Driver | 754302 | `pg_advisory_xact_lock` around the SQL baseline |
| Matching | 754303 | `pg_advisory_xact_lock` around the SQL baseline |

The lock is transaction-scoped: a crash rolls back the DDL and releases the
lock; a retry is idempotent. `DriverLifecycleMigrationTests` and
`MatchingMigrationConcurrencyTests` exercise two concurrent starts on an empty
database and verify existing rows survive a repeated start.
