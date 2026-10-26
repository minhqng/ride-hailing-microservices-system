# Deterministic fault campaigns

Owner: B (Driver/Matching). Platform fault contracts remain C-owned.

## B-W11 REL-01 / REL-02

`b-w11-rel01-rel02.sh` runs the real Driver and Matching service images with
PostgreSQL, RabbitMQ and Redis in an isolated Docker network. It does not use a
transport stub for the decisive call.

- **REL-01**: Matching writes `IN_FLIGHT` + dispatch fence, sends Reserve over
  real HTTP, Driver commits the reservation and its `DriverReservedV1` Outbox
  intent, then the Testing-only
  `Driver.AfterReserveCommitBeforeResponse` barrier suppresses the response.
  Matching times out into `UNKNOWN` without advancing; the same
  `reservationId` is reconciled to `RESERVED`.
- **REL-02**: Matching has durable `IN_FLIGHT`, Driver has committed, and the
  script sends `SIGKILL` to the Matching container. It verifies exit code 137,
  starts a NEW Matching container against the same PostgreSQL/RabbitMQ, and
  reconciles the persisted reservation identity.

The barrier is B-owned because C PR #41's shared fault platform is not on
`main`. It keeps the frozen fault name and the same `HitAsync` shape so it can
delegate to the shared contract later. Production resolves a disabled no-op;
the seam requires the explicit `Testing` environment plus
`RIDE_FAULT_CONTROL_TOKEN`, and every hold is bounded.

### Run

```bash
./scripts/fault/b-w11-rel01-rel02.sh
SKIP_BUILD=1 ./scripts/fault/b-w11-rel01-rel02.sh   # reuse rhw11b-* images
KEEP_CONTAINERS=1 ./scripts/fault/b-w11-rel01-rel02.sh
```

Requires bash, Docker, curl and Node.js. Raw output is written below
`evidence/member-b/w11-fault-proofs/raw/` and is gitignored; curated evidence
lives at the evidence bundle root.

### Limitation

The restarted Matching process uses the W11 test-only reconcile trigger. The
service does not yet perform unattended recovery/redelivery, so the proof is a
real process kill + real DB reload + explicit recovery invocation, not an
automatic recovery claim.
