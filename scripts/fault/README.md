# Deterministic faultpoint platform and HA harnesses

- `ha-up.ps1 -Applications` forms the disposable three-node RabbitMQ 4.3.6
  cluster, starts Payment/Notification/Prometheus, requests quorum membership
  growth, and fails unless every declared quorum queue reaches three online
  members.
- `rel15-broker-interruption.ps1 -Execute` stops Payment and the primary node,
  publishes an exact-duplicate probe batch through a surviving node, requires
  the queue backlog to become visible, then restores both boundaries. PASS
  requires fresh Payment connections, a drained queue, exactly one operation,
  provider effect, dedup marker and confirmed result Outbox row per unique
  command, and no `FAILED_VISIBLE` result.
- `rel16-one-node-loss.ps1 -Node rabbitmq-3 -Execute` refuses to proceed when
  the target is quorum-critical, stops one node, requires exactly 2/3 online
  members of `payment.commands.q` during the outage, runs the same duplicate
  workload while that node is down, verifies the consumer drains and the
  Payment/Outbox state converges exactly, then restores the node and waits for
  all 3/3 members to rejoin before reporting PASS.

REL-15/16 read the management API using `RABBITMQ_USER` / `RABBITMQ_PASSWORD`
from the environment when supplied (local defaults match `compose.yaml`).
These scripts mutate only the explicitly selected Compose project. Use an
isolated `COMPOSE_PROJECT_NAME`; never point them at production.

## Integrated controls

The shared provider gates consumer commit-before-ACK, confirmed Outbox publish
before mark, Payment provider commit before response, Driver reserve commit
before response, and Saga commit before dispatch. Hosts use their effective
environment: activation is accepted only in `Testing` or `Development`.
Production remains inert even when activation variables are present.

Set `RIDE_FAULTPOINTS` to comma-separated canonical names. For external process
control, set `RIDE_FAULT_SIGNAL_DIRECTORY` to a fresh harness-owned directory.
The provider atomically writes `faultpoint-<name>.reached`; the harness creates
`faultpoint-<name>.release` to continue. Holds are bounded at 30 seconds by
default. Use a fresh directory per scenario to avoid reusing a release marker.
There is no shared HTTP activation endpoint.

The Trip marker-file adapter and Driver token-controlled adapter remain
compatible with the original process harnesses. The Driver adapter implements
the shared interface while retaining its stricter Testing/control-token gate.
Trip uses its existing adapter when configured and otherwise the shared provider.
The Driver control endpoints are Testing-only and require the control token.

Run `bash scripts/fault/team-fault-campaign.sh` on a clean Linux checkout with
.NET 10, Docker, Python 3 and Node.js. It executes five controlled passes of
Trip process faults, shared delivery/Payment windows and the Driver/Matching
campaign. CI runs this additional campaign for the final W11 integration PR.
Outputs retain checkout SHA/tree and TRX counts, reject zero discovery/skips,
and stay under ignored evidence output directories.

## Original owner handoff context

The notes below retain the source contributions' historical claim boundaries.

Team-shared fault infrastructure lives here once C delivers the W11 platform
(Doc 05C §30: `FaultPointProvider`, `Consumer.AfterCommitBeforeAck`,
`Outbox.AfterPublishBeforeMark`, `Payment.AfterProviderEffectBeforeResponse`,
environment gating, harness signals/barriers).

## Current state (W11)

C's shared platform is not delivered yet. Member A has landed the one
A-owned named faultpoint it needs for its W11 crash/restart proofs, with a
frozen integration contract so C's provider can adopt it without a Trip
rewrite:

- Name: `Saga.AfterPersistBeforeDispatch`
- Location: the owning service's Outbox relay pre-publish seam
  (`OutboxRelayOptions.BeforePublish`) — necessarily after the local commit,
  before dispatch.
- Scope: the saga's outbound payment dispatches only.
- Activation: default-off config (`TripFaultPoints:*`) AND non-Production
  host environment; enabling in Production fails startup; no HTTP surface.
- Signaling: `reached` marker file + `release` file in a test-owned signal
  directory; bounded wait with a hard deadline.

Authoritative documentation: `src/Services/Trip/RideHailing.TripService/
Integration/FaultPoints/TripFaultPointGate.cs` (contract on the class) and
the Trip service README (W11 section). Do not add a second faultpoint
framework next to it — extend the frozen contract or wait for C's provider.
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
