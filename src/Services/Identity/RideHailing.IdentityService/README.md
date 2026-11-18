# Identity Service

Identity owns user accounts, roles and signed access grants. It does not own
Driver, Trip or Payment state.

- Database: `identity_db` (role `identity_app`).
- Depends on: `ConnectionStrings__Postgres`, RSA signing/validation keys and
  service credentials described in
  [authentication runtime](../../../../docs/authentication-runtime.md).
- Contract: [domain invariant contract](../../../../docs/01-domain-correctness-contract.md).

Registration and login use the owned PostgreSQL account store and ASP.NET Core
password hashing. Short-lived user and service JWTs are RSA-signed and
validated independently by the Gateway and every receiving service.

| Method | Route | Purpose |
|---|---|---|
| POST | `/auth/register` | Create a Rider or Driver account |
| POST | `/auth/login` | Issue a signed user token |
| POST | `/connect/token` | Issue a signed service grant |

The service exposes `/health/live`, `/health/ready` and `/info`. Readiness
reports the owned database state.
