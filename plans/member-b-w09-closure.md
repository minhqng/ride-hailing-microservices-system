# Member B W9 closure

Outcome: close duplicate safety, REL-02 automation base, independent review and evidence for W9 only.
Constraints: preserve 0b1611c and 03c0438 atop 7a0575a; local commits only; no A/C production edits; real infrastructure for integration claims.
Non-goals: W10/W11, full fault campaign, remote mutation.
Acceptance: six duplicate invariants, persisted same-R recovery without next candidate or false NO_DRIVER, requested suites and Release build, evidence with honest limitations and external gates.

1. Preflight VERIFIED: clean feature/B-W09-driver-outbox at 03c04380ec8ddd7409e0dce934c155f853922779; both ancestors present. PR35 open at 7a0575a51a89ba32bd3017afd779355ed2da9576 (connector read; local gh lacked authentication).
2. VERIFIED: consumer/producer inspection and separate read-only review of both full commits; follow-up reviewed fixes.
3. VERIFIED: B-owned projection conflict receipts, safe legacy NO_DRIVER replay repair, full Matching intent semantic validation, same-R real-DB recovery proof and three Trip event-type receipt tests. No A/C production edits.
4. VERIFIED: 880 selected unique tests pass, focused subsets pass, Release build passes. Earlier assertion/infrastructure/runner failures retained and diagnosed in the manifest.
5. VERIFIED: implementation committed separately at d825920d51aaae39ecff31a72540bf45cd800b4a; evidence/docs follow separately. External approval/rollout/process-kill gates remain explicitly open.

Local closure complete. Verdict: B-W09 CODE COMPLETE — EXTERNAL GATES REMAIN.
See [evidence](../evidence/member-b/w09-outbox/README.md),
[manifest](../evidence/member-b/w09-outbox/manifest.md), and
[handoff](../evidence/member-b/w09-outbox/handoff.md).

Scenario dimensions: timing, state transitions, error cascades, data integrity, integration, business logic. User types/auth/compliance are outside this internal messaging change; input and scale are covered by existing parser/bounded retry tests; environment is real pinned containers.

| Scenario | Severity | Invariant/test target |
|---|---|---|
| Exact publication replay | High | One receipt/effect, unchanged identity/version |
| Reused ID with changed semantic content | Critical | Reject before mutation, including projection |
| Commit before ACK disconnect | High | Durable receipt/effect converge on redelivery |
| Stale release/reserve after newer state | Critical | Version and terminal guards prevent regression |
| Interrupted Matching after Driver commit | Critical | Durable IN_FLIGHT, same R, same attempt, no next candidate |
| Terminalizer races unresolved/recovered attempt | Critical | No NO_DRIVER or failure Outbox |
| Receipt/projection key has wrong Redis type | High | No partial projection or receipt |

Rollback: revert only new local commits if needed, never rewrite preserved commits. Raw logs stay ignored under evidence/member-b/w09-outbox/raw/.
