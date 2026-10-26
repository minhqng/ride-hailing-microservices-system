# B-W11-09 arbitrary-sleep audit

Scope: B-owned Driver/Matching unit and integration tests plus
`scripts/fault/`. Search performed on final source
`c3907a93404d9016a2afefd5b25434f0bacfb49f`.

## Verdict

- `Thread.Sleep`: **none**.
- Fixed sleep used as the correctness oracle: **none**.
- Shell `sleep`: only inside deadline-bounded condition/health loops.
- `Task.Delay`: all uses are (a) a bounded condition poll with a deadline,
  (b) an intentionally blocked consumer/handler fixture ended by
  cancellation, or (c) a bounded message-read poll. None is the final oracle.
- The Phase 2 CON/REL proofs use barriers, receipts, broker ACK counts,
  process exit codes, and durable DB/projection rows rather than scheduling
  windows.

## Inventory and classification

| Location | Use | Classification |
|---|---|---|
| `DriverCompensationTests.cs` — `Until` | 25 ms predicate poll, 40 s deadline | bounded condition poll |
| `DriverCompensationTests.cs` — crash-before-ACK fixture | handler blocks forever after commit; cancellation ends it | deliberate blocked fixture, not timing oracle |
| `DriverEventPublishTests.cs` — broker read helper | 100 ms read poll, cancellation-aware | bounded message poll |
| `DriverLifecycleBrokerTests.cs` — `Until` | 25 ms predicate poll, 40 s deadline | bounded condition poll |
| `DriverLifecycleBrokerTests.cs` — crash-before-ACK fixture | handler blocks forever after commit; cancellation ends it | deliberate blocked fixture |
| `BrokerConsumeTests.cs` — `PollUntilAsync` | 100 ms condition poll, 30 s deadline | bounded condition poll |
| `BrokerConsumeTests.cs` — broker-unavailable test | replaced a prior fixed 5 s window with a count of logged retry failures | observable retry barrier |
| `MatchingOutboxRelayTests.cs` — relay poll | 50 ms condition poll with deadline | bounded condition poll |
| `MatchingTerminalOutboxTests.cs` — terminal poll | 20 ms condition poll with deadline | bounded condition poll |
| `RedisOutageRecoveryTests.cs` — reconnect poll | 100 ms ping poll, 30 s deadline | bounded health poll |
| `b-w11-rel01-rel02.sh` — `wait_for` / container-stop loop | 0.25 s poll interval | bounded condition poll |

## Phase 2 barriers replacing scheduling

- **CON-02**: `Barrier(2)` plus a controlled release gate. The loser is
  released only after the winner commits with the same starting version.
- **CON-04**: real broker ACK count and durable command receipts. The late
  disposition and original receipt are read from PostgreSQL after broker
  processing, before any direct replay.
- **REL-12/13**: authoritative and stale phases are separated; the next phase
  starts only after every envelope in the prior phase has the expected
  projection receipt fingerprint. Presence of every receipt proves each
  message reached the real handler, even when its outcome is stale.
- **REL-14**: TCP gate close/open plus bounded Redis ping/condition polls.
  Degraded status and recovery are based on the production candidate result
  and durable projection/authority state, not elapsed time.
- **REL-01/02**: faultpoint-reached signal, matching DB attempt state, Driver
  DB/Outbox, HTTP timeout, and SIGKILL exit code/identity are the boundaries.
  The shell sleeps are only poll intervals inside bounded waits.

## Remaining acceptable delays

The remaining `Task.Delay` calls are not the source of truth: every assertion
reads a durable row, Redis projection/receipt, process state, or an observable
log condition after a deadline-bounded wait. Production reconnect/retry loops
in `BrokerConsume`, `DriverCommandConsumerHost` and the Outbox relay are
intentionally indefinite with bounded per-attempt backoff; they are outside
the test-oracle scope and are not used to decide a proof result.
