## Intent
What distributed-system problem is this PR closing?

## Contract / DSI / DSL
Docs + invariants:

## Mechanism
What changed?

## Failure window
What can go wrong across crash/timeout/redelivery windows?

## Tests
Unit:
Integration:
Concurrency/Fault:
> Concurrency, crash-replay and broker-recovery coverage is owned by the
> integration suites (`--filter` on the fault cases). If this change touches a
> failure window, name the suites you ran and link their output.

## Final authoritative state
What DB assertion proves correctness?

## Evidence
Link/path:

## Reviewer focus

## Scope intentionally not included

---

### CI gates on this PR

| Job | What it proves |
|---|---|
| `hygiene` | No transient artifacts, evidence anchored to real source |
| `build-test` | Release build + full .NET suite + TRX discovery gate |
| `compose-config` | Dev/release/HA render; release overlay fails closed without secrets |
| `platform-smoke` | Real containers, service isolation, security and topology |
| `release-smoke` | Production overlay: migrations, JWT journeys, outage and crash replay |

All five must be green. A skipped or missing job is not a pass.
