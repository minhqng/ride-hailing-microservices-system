# Member C W07–W11 completion matrix

Date: 2026-10-25

| Week | Status | Evidence / remaining dependency |
|---|---|---|
| W07 | Implemented | Classified finite retry/DLQ policy, safe error envelope, poison/schema handling, deterministic Payment timeout-after-effect and same-key reconciliation are present and covered by unit/integration tests. Real PostgreSQL replay needs Docker. |
| W08 | Implemented; real-broker rerun blocked | Confirmed mandatory publisher, manual ACK-after-handler, bounded prefetch, envelope/version validation, trace propagation, quorum topology, and Rabbit integration tests are present. Docker engine unavailable. |
| W09 | Implemented; container proofs blocked | Durable processed-message schema/API, atomic caller transaction, SKIP LOCKED Outbox claim, confirmed publish/mark, stable replay identity, bounded visible failure, and metrics are present. Deterministic in-memory relay proofs pass 5/5. |
| W10 | Payment boundary implemented | Payment runtime now consumes all three commands and atomically commits dedup + Payment result + Outbox event. Persisted Trip Saga, REL-09/10, and full ride are owned by the not-yet-present Member A runtime. |
| W11 | C-owned platform implemented | Environment-gated barriers, signals/releases, bounded waits, three C fault windows, and Driver reserve handoff point are wired. Saga dispatch point is named but cannot be placed until the Saga exists. Deterministic suites pass 5/5; real REL-05/06/08 and peer rerun need Docker/peer. |

No unrelated `ride-hailing-dispatch-platform*` directory was modified.
