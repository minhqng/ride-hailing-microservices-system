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

## B-W11 Phase 2 — full deterministic campaign

`b-w11-full-campaign.sh` is the single clean-checkout acceptance command. It
runs five consecutive controlled passes of:

- REL-01 / REL-02 through the real service containers (Phase 1 script).
- CON-02 both deterministic GoOffline-vs-Reserve winning schedules, with
  Outbox-intent assertions (`DriverCon02DeterministicTests`).
- CON-04 direct exact-token release and a real RabbitMQ
  `ReleaseDriverReservationV1` late-arrival/receipt replay
  (`DriverRaceFencingTests`, `DriverCompensationTests`).
- REL-12 / REL-13 through the real `DriverProjectionHandler` + Redis and
  through real RabbitMQ projection-queue deliveries
  (`DriverProjectionStalenessHandlerTests`, `BrokerConsumeTests`).
- REL-14 with a real disposable Redis container network-isolated by a
  test-controlled TCP gate. The production candidate path must degrade with
  zero candidates/reservations and unchanged Driver authority; after the gate
  reopens the test proves surviving projection data and event-driven
  reconstruction separately (`RedisOutageRecoveryTests`).

```bash
./scripts/fault/b-w11-full-campaign.sh
PASSES=1 ./scripts/fault/b-w11-full-campaign.sh       # smoke only
ALLOW_DIRTY=1 PASSES=1 ./scripts/fault/b-w11-full-campaign.sh
```

Each pass gets a fresh container namespace and fresh GUID identities. A
failed or zero-test scenario stops the streak and leaves its logs under
`evidence/member-b/w11-fault-proofs/raw/five-pass-*/pass-N/`. The final
`five-pass-summary.json` records the source SHA/tree, source status, per-pass
scenario results, elapsed time and identity lines. Multi-pass acceptance
requires a clean tree; `ALLOW_DIRTY=1` is accepted only for a single-pass
diagnostic and the summary is marked `DIAGNOSTIC`.

The runner clears `MATCHING_TEST_POSTGRES`, `DRIVER_TEST_POSTGRES`,
`RABBITMQ_TEST_CONNECTION`, `REDIS_TEST_CONNECTION`,
`Redis__ConnectionString` and `TRIP_TEST_POSTGRES` so a pass cannot silently
run against user-owned infrastructure. Only Project B's isolated containers
are created/removed; no user-owned Redis or other container is stopped.

REL-14's partition is a test-controlled TCP gate in front of the disposable
Redis container: active client sockets are closed, the production candidate
path degrades, and the gate is reopened. Redis process/data stay alive, so the
test explicitly reports `survived` separately from
`eventReplayReconstructed`. The broker projection tests require a receipt for
every published envelope, so stale deliveries cannot pass by being dropped.
