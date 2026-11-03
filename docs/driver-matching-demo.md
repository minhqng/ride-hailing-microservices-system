# Driver/Matching release demonstration and team handoff

Run this from a clean checkout of the release source recorded in
[the release manifest](../evidence/member-b/final/release/manifest.json).
Docker Desktop Linux containers, Git, Python3 and free default Compose ports
are required. The runner uses the .NET10 SDK image; no host SDK or author-home
path is needed. It owns only `team-w12-b-release`, a labeled SDK container and
a unique temporary package volume. It refuses an existing project rather than
resetting someone else's data.

```powershell
python scripts/demo/member-b-live.py --repeats 3
```

The operator command performs a full Release build and all four B regression
projects with real PostgreSQL/Redis/RabbitMQ, builds application images once,
then runs three independent empty-volume technical rehearsals. Each trial
reuses those source-built images, records actual B/demo durations, and tears
down its own volumes. Output defaults to ignored
`evidence/member-b/final/release/outputs/<run>/`. Inspect its `summary.json`
and trial JSON before curating durable evidence.

## Ready-to-use B sequence

1. **Entry from A:** identify the stable Trip/matching generation and explain
   that Driver owns reservations while Trip owns the accepted assignment.
2. **CON-01:** show ten authenticated calls released through the existing
   common barrier. Display one winner, nine expected conflicts, zero unexpected
   failures, exact reservation/Trip identity, one DriverReserved Outbox effect
   and independent AUD-01. The runner releases exactly that identity and
   offlines the demonstration Driver before the shared journey.
3. **Redis:** before cleanup, replay the actual earlier accepted AVAILABLE
   state version and location sequence into the production Redis Function.
   Show RESERVED/current version and sequence21 surviving the older inputs,
   and identical authoritative Driver snapshots before/after replay. This is
   an operator replay at the Function boundary; full handler/receipt behavior
   is separately covered by real Redis integration tests.
4. **REL-01:** execute the existing real Kestrel post-commit faultpoint test.
   Display the `AUTHORITY_PROOF` record: committed R, persisted UNKNOWN R,
   untouched D2, conclusive same-R round/Driver result and one Outbox intent.
   The test uses production Driver endpoints/repository and Matching typed
   client/orchestrator with a bounded barrier. Separate production-worker and
   STARTED/COMPLETED process-restart tests run in the full B regression.
5. **Exit to A/C:** show the three authenticated complete/cancel/decline
   journeys and AUD-01..08 against owning authorities. A presents Trip/Saga
   state; C presents Payment effects, Outbox confirms, broker/trace observations.
   Retain exact Trip/R/message/trace IDs from that trial, not a previous run.

Spoken Redis explanation: “The Function atomically compares ordering metadata
and changes the hash/GEO projection. It does not reserve a Driver or transact
with PostgreSQL. Old candidates can waste attempts; Driver still decides.
Reconnect/function reload differs from lost-data reconstruction, which needs
explicit event replay. Startup projection-registration failure may require a
Matching restart.”

## A/C boundary review and run-of-show

The read-only successor comparison uses adjacent owner branches, not cumulative
diffs against main. A-W14→A-W15 adds report/freeze evidence; A-W15→A-W16 adds
release/demo assets. C-W14→C-W15 adds release configuration/CI/security/report
assets; C-W15→C-W16 adds release/demo assets. They introduce no new B-facing
application contract. They are not merged or adopted by this contribution.

| Boundary | Inputs and entry signal | Owning final state / exit signal | Recovery if segment fails |
| --- | --- | --- | --- |
| A→B | Stable tripId, current matchingRound, valid authenticated intake | Matching round + selected attempt agree with Driver R; Trip guards current/legal DriverReserved | Retain exact IDs; never manufacture assignment or advance an unresolved attempt |
| B→A | DriverReserved exact R, round and event identity; cancel/cleanup identity | Trip terminality/current-round guard, exact cleanup, AUD-04 Driver/Trip agreement | Inspect late-event cleanup and tombstones; do not reactivate cancelled Trip |
| B↔C | Versioned envelope identity, durable Outbox, confirm outcome, manual ACK after durable effect | Stable message IDs, dedup receipts, one effect; finite retry/DLQ and trace observations | Restore provider and allow owning relay/worker recovery; operator redrive is explicit |
| C→all | Actual broker membership/leader observations and matched authority IDs | Queue topology plus owning DB states, not broker depth alone | Classify environment failure separately from business correctness; retain failed attempt |

Integrated implementations/tests prove current-round guards, cancellation
terminality, exact release, late DriverReserved cleanup, stable envelope IDs,
publisher confirms, manual ACK/durable dedup, Outbox and retry ownership.
Use [the authenticated gate](../scripts/test/core-runtime-gate.py),
[messaging runtime](messaging-runtime.md), and the
[G4/G5 proof boundaries](../evidence/team/w14-integration/README.md).

**A's successor `member-a-mock-workflow.ps1` directly inserts labeled mock
Trip/Saga rows. It is a dashboard aid, not an end-to-end authority rehearsal.**
C's `member-c-mock-traffic.ps1` sends routed management-API commands with
deliberate duplicates; `routed=true` alone is not an AMQP publisher-confirm
proof or an integrated Trip journey. Use the integrated production-handler
gate for the scripted team technical rehearsals. Do not execute peer mock
scripts against the shared demo dataset and count them as real workflows.

No A/C peer sign-off is asserted. Actual human role handoffs and three human
team rehearsals remain PENDING until participants perform them. Automated
execution records technical runtime evidence only.

## Fallback and release authority

If the environment fails, first preserve that run's failure and restore its
owned providers; rerun with a new empty-volume trial. Never clear UNKNOWN or
edit business rows to recover a presentation. For a bounded isolated replay:

```powershell
# In the runner's built SDK context, execute the same recorded integration test:
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests -c Release --filter FullyQualifiedName~LostReserveResponseHttpTests
```

This fallback requires a native .NET10 SDK or the documented SDK Docker mount
used by the main runner. Historical fallback evidence remains at
[W11](../evidence/member-b/w11-fault-proofs/README.md) and
[reconciliation safety](../evidence/member-b/w12-reconciliation-safety/README.md),
bound to their original source. A replay or recording is labeled as such and
never described as a live successful execution.

There is no final release tag in the discovered remote tag set. Doc04 §27
uses `v1.0.0` as its final-tag example. B supplies the tested source/tree and
evidence contract; the lead must first integrate approved A/B/C work, verify
that exact candidate's CI/source/evidence and explicitly approve the final tag.
No final tag is created or moved by this task. The W16 branch is a tested B
handoff candidate, not an approved team-wide final release.

After explicit lead approval, the lead can fetch the candidate, check its exact
SHA/tree and CI, confirm `git ls-remote --tags origin refs/tags/v1.0.0` is empty,
then run `git tag -a v1.0.0 <approved-exact-sha> -m 'Verified final release'`
and `git push origin refs/tags/v1.0.0`. Never move an existing tag or substitute
an unreviewed newer main tip. These are instructions for the authorized lead,
not actions performed here.
