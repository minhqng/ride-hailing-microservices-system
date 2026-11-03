# TEAM W16 release evidence

Scope: integrated release-candidate assembly, cross-owner contract and journey
verification, release/security gates and rehearsal accounting for Member A
(PR #50), Member B (PR #63) and Member C (PR #55) on W15-verified main.

- Base main: `6ef99a738f7a3360f2a697dd7695df852365c880`
  (tree `7448c1d2cee05aa5b77c880e6031746fd8311a41`)
- Verified source commit: `17ca928d5fdd9ef64bba16574503045317033b27`
  (tree `59a740ec266acf219bfbbd9e75d98080b6926e06`)
- This directory is an evidence-only commit. Everything outside `evidence/`
  is unchanged from the verified source commit.

## What was recovered

- A: release/demo ledger and two release scripts, patch-identical.
- B: release rehearsals, live demo, defense/demo docs and one Matching test
  assertion, patch-identical except the docs index conflict resolution.
- C: release gate, mock traffic demo and C W16 evidence, patch-identical.
- PR #56: no unique W16 code recovered; every listed correction is already in
  main or superseded by newer C/W14 content. See `source-mapping.json`.

## Verification

- Release build (Docker SDK 10.0): PASS, 0 errors, 14 pre-existing analyzer
  warnings.
- Secret scan: PASS on 1300 tracked paths.
- Release overlay: positive render PASS; secretless render fails closed.
- B claims validator PASS (174 digest checks); B freeze hashes 7/7 and release
  hashes 14/14 PASS with canonical-LF bytes.
- B source anchor: `verify-source-anchor.sh 598d893` PASS on the B branch;
  C/A W16 tips have no non-evidence drift after their source commits.
- Hygiene 29 changed paths clean; anchor fixtures 12/12; perf harness 35/35.
- B real-provider harness on the integrated candidate: 564 provider tests
  passed; three empty-volume trials PASS at 74.21s / 74.53s / 84.86s, each
  with one winner/nine conflicts, same-reservation UNKNOWN recovery and
  AUD-01..08 PASS. See `test-output.txt` and
  `final-state/rehearsal-summary.json`. `humanParticipation` is
  `NOT PERFORMED`.
- Exact-tree GitHub CI: run 41655374106 on PR #65 passed hygiene,
  compose-config, build-test and platform-smoke; merge-checkout tree
  `a85b85b3a6d5d33098a6cc792f0a55d6a4b99197` equals the candidate tree.
  TRX: 1223 passed/0 failed/1 opt-in Identity NotExecuted; dedicated Identity
  7/7; 55 security checks; core-runtime scenarios + AUD-01..08 PASS.

## Cross-owner evidence

`release-evidence.md` maps every required contract and journey to its
implementation and runtime/source-bound proof. `task-status.md` contains the
full A/B/C/TEAM matrix and adjudication.

## What this bundle does not claim

- No human team rehearsal, spoken timing, role handoff, oral defense or peer
  sign-off. B's and the integrated runs are automated technical rehearsals.
- No final tag or GitHub Release. `docs/release-candidate.md` prepares but does
  not publish it.
- No whole-system/multi-region HA and no production capacity claim.
- No new live HA campaign: W15/W16 do not change runtime source, so accepted
  W14 G5 evidence retains its original source identity.

## Acceptance summary

The source is an automated-technical release candidate: build, tests, security,
release configuration, contracts and journeys are verified on the exact tree.
The open items are human/course gates and tag authorization.
