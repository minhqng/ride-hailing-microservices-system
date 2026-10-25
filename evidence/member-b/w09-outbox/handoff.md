# W9 handoff

Verdict: **B-W09 CODE COMPLETE — EXTERNAL GATES REMAIN**.

Code commit: `d825920d51aaae39ecff31a72540bf45cd800b4a`.
Preserved lineage: `7a0575a` → `0b1611c` → `03c0438` → code commit.
The evidence commit follows separately. See [README](README.md) for the complete
01–10 checklist and [manifest](manifest.md) for commands, totals and retained failures.

## Operator / team gates

1. **C / lead:** review and integrate PR #35 at the exact verified dependency
   revision. It remains open. The local shared suite passed 60 tests including
   disjoint relay claims; this does not grant approval to merge or mutate refs.
2. **A / lead:** normal human peer review and CI for the B branch, including the
   three event-type Trip contract tests. No verified A-owned production defect
   was found, and no A/C production code was changed. No defect is being hidden
   behind a TEAM_DEPENDENCY label.
3. **Lead / B rollout decision:** old pre-W9 RESERVED rows do not necessarily
   have an Outbox intent. Current exact reserve replay preserves identity but
   does not backfill absent historical intents. Agree on backfill or an
   authority-based rebuild before claiming historical deployment convergence.
4. **Lead / B rollout:** deploy the projection updater and Lua function together;
   older hosts must not overwrite the shared function with the pre-receipt
   version. Derived receipts have no TTL and grow until a deliberate complete
   projection rebuild. Redis loss/rebuild resets conflict history. This is not
   a new durable PostgreSQL inbox or a business authority.
5. **W11 owner, future authorization:** external process-kill/restart REL-02
   orchestration, deterministic faultpoint handshake at the post-Driver-commit
   boundary, bounded persisted-state polling, actual restart/dispatch over HTTP
   and broker, final audits. The W9 base provides the same-R DB oracle to reuse.
   None of that W11 implementation was started here.

## Local completion boundaries

- No W10/W11 feature work, push, merge, reset, rebase or force-push.
- Existing OnTrip/TripCompleted domain facts have no production mutation/command
  path to wire here; their future workflow remains outside this closure.
- No exactly-once guarantee. Stable IDs, local transactions, consumer receipts,
  guarded transitions and same-R reconciliation supply the verified safety.
- No outstanding confirmed B defect in the inspected changed production scope.
  The full failure history remains in the manifest; final selected suites pass.
- Independent reviewer and simplification pass made no edits. Review issues were
  fixed by the controller, then checked again. Simplification found no justified
  reduction; a comment encoding issue was repaired without behavior change.

## Reproduction / resources

Use the manifest's Docker SDK configuration and focused filters first. Allow
Testcontainers to create disposable databases/brokers; do not point these suites
at the mobile stack or another developer's data. Do not run root RabbitMQ CLI
probes in starting fixture containers. The verification containers have exited;
their fixtures disposed resources. The named NuGet volume is only a local cache.
No user/application container was stopped. Raw logs are local and ignored, so
share them separately under operator control if a reviewer needs full output.
