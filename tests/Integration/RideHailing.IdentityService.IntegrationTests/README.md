# Auth negative tests (SEC-01–04)

Executable negative-test suite for authentication/authorization on real
business APIs. The seven cases run against an owner-supplied fixture and assert
that rejected requests leave authoritative state unchanged.

Spec: `../../../docs/03-verification-experiments-demo.md` SEC-01–04.
Test: `AuthNegativeTests.cs` in this directory.

## Status

Opt-in in the solution-wide regression: the theory skips unless
`RIDE_AUTH_FIXTURE` is set, so the full run reports one documented skip.
`scripts/test/security-gate.py --identity-tests` generates the fixture and runs
all seven cases against the live stack. Missing fixture fields or cases fail
once enabled. Passing these tests is evidence for the SEC-01–04 boundaries, not
a substitute for owner review of route semantics, tokens and snapshot coverage.

## Activation

1. Start the real APIs, Identity and owned DBs with deterministic users
   (e.g. Rider A, Rider B, Driver A). Keep unrelated writers out of fixture
   scope.
2. Create a local JSON fixture file using the shape below (or let
   `security-gate.py --identity-tests` generate it). Replace routes,
   request bodies, environment variable names and SQL with the actual owner
   contracts.
3. Set `RIDE_AUTH_FIXTURE` to its absolute path. Provide tokens and the owner
   DB connection string via environment variables; never commit their values.
4. Run `dotnet test tests/Integration/RideHailing.IdentityService.IntegrationTests -c Release`.

```json
{
  "baseUrl": "http://localhost:8103",
  "controlPath": "/REPLACE_WITH_AUTHENTICATED_READ",
  "controlTokenEnvironment": "RIDE_TOKEN_VALID",
  "databaseEnvironment": "RIDE_AUTH_OWNER_DB",
  "snapshotSql": "REPLACE_WITH_DETERMINISTIC_OWNER_STATE_SELECT",
  "cases": {
    "missing-token": { "method": "POST", "path": "/PROTECTED_ROUTE", "body": "{}", "tokenEnvironment": null },
    "invalid-signature": { "method": "POST", "path": "/PROTECTED_ROUTE", "body": "{}", "tokenEnvironment": "RIDE_TOKEN_BAD_SIGNATURE" },
    "expired-token": { "method": "POST", "path": "/PROTECTED_ROUTE", "body": "{}", "tokenEnvironment": "RIDE_TOKEN_EXPIRED" },
    "malformed-token": { "method": "POST", "path": "/PROTECTED_ROUTE", "body": "{}", "tokenEnvironment": "RIDE_TOKEN_MALFORMED" },
    "wrong-role": { "method": "POST", "path": "/DRIVER_ONLY_ROUTE", "body": "{}", "tokenEnvironment": "RIDE_TOKEN_RIDER_A" },
    "foreign-object": { "method": "GET", "path": "/RIDER_B_OBJECT", "body": null, "tokenEnvironment": "RIDE_TOKEN_RIDER_A" },
    "spoofed-actor": { "method": "POST", "path": "/ACTOR_BOUND_ROUTE", "body": "{\"actorId\":\"OTHER_ACTOR\"}", "tokenEnvironment": "RIDE_TOKEN_RIDER_A" }
  }
}
```

## Acceptance requirements

- Authenticated GET positive control must return 200 on an actual protected
  business route, never `/health` or `/info`. Missing services/routes must
  fail.
- SEC-01: missing, malformed, expired or invalid-signature tokens => 401.
  Other request fields must be valid so the intended security boundary is
  tested.
- SEC-02: valid Rider token invoking a Driver-only function => 403.
- SEC-03: Rider A requesting Rider B's object => 403 in this fixture. If the
  API intentionally conceals existence with 404, agree and change that case
  explicitly.
- SEC-04: an actor-bound request supplying another actor => 403 in this
  fixture. If the canonical API ignores the supplied actor and uses the
  authenticated identity, replace this case with an assertion on the actual
  affected actor; do not count a generic success as proof.
- `snapshotSql` must return one nonempty deterministic scalar containing all
  relevant business state, including row counts and ordering. Constant queries
  or logs do not qualify. Queries execute in read-only transactions using the
  owner DB role.
