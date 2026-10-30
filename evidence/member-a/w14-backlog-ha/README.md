# A-W14 — backlog and HA application review

This week validates the Trip application after C-owned broker interruption and
quorum-node scenarios. Member A does not infer whole-system HA from an
application observation.

Acceptance sequence:

1. Record Trip/Saga/Outbox state before the fault.
2. During broker interruption, verify committed intents remain pending and no
   row is falsely marked `PUBLISHED` without a confirm.
3. Restore the broker and verify backlog drains while the same stable message
   and operation identities are retained.
4. Verify pending Saga work resumes from `ride_sagas`, not process memory.
5. Run `member-a-recovery-check.ps1` and the invariant auditor after recovery.

```powershell
./scripts/evidence/member-a-recovery-check.ps1 -MetricsOnly
# With Docker exposed to the shell, omit -MetricsOnly to run the auditor lane.
# Defaults require outbox_pending=0, oldest age <=60 s and failed-visible=0;
# override explicitly for a different measured baseline.
```

Allowed claim: **Trip Outbox/Saga recovered under the recorded broker
scenario.** Not claimed: the entire platform is highly available.
