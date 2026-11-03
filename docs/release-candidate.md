# Release candidate — distributed ride-hailing platform

Status: **source release candidate assembled; not tagged or published.**

This document prepares the final release. It does not create a Git tag or a
GitHub Release. Creating either requires separate project-lead authorization
after the course's human rehearsal and oral-defense gates are complete.

## Candidate identity

- Suggested version/tag: `v1.0.0` (Doc 04 §27 example; **not created**).
- Base: W15-verified main.
- Candidate source: the commit containing this document. The exact commit,
  tree, CI checkout and evidence hashes are recorded in
  `evidence/team/w16-integration/manifest.json`.
- Source scope: W09–W16 product and evidence on W14 accepted main. W15/W16 add
  reports, evidence, release configuration, CI gates, demo tooling and one
  Matching integration test; they do not add a service, broker, database
  technology, orchestration framework or new product feature.

## Changelog (W11–W16)

- W11: deterministic faultpoint and real process crash/restart proofs.
- W12: driver/matching recovery repair, observability, authorization negatives,
  invariant auditor and G4 acceptance.
- W13: frozen-environment performance campaigns E01–E04 with correctness
  checks and measured populations.
- W14: quorum broker HA, backlog recovery and G5 real-provider evidence.
- W15: feature freeze, member technical reports, team report, fail-closed
  release overlay, secret scanner and integration evidence.
- W16: release/demo tooling, defense workbook, deterministic release gate,
  exact-tree CI, automated technical rehearsals and this release candidate.

## Member contributions

- A: Trip aggregate/Saga/Outbox, compensation and capture recovery, Trip
  observability/auditor, W15/W16 reports and demo/gate scripts.
- B: Driver reservation authority, durable Matching/UNKNOWN recovery, Redis
  derived projection, E01–E04 measurements, clean-clone freeze evidence and
  W16 live rehearsal/defense material.
- C: Payment idempotency, messaging reliability, deduplication/Outbox relay,
  DLQ/retry ownership, observability, broker HA harness, release overlay,
  secret scanner and release gate.

## Build and deploy

```bash
python scripts/security/init-local-auth.py
docker compose --profile obs up -d --build
python scripts/test/platform-smoke.py
python scripts/test/security-gate.py --identity-tests
python scripts/test/core-runtime-gate.py
python scripts/test/payment-smoke.py
python scripts/test/topology-smoke.py
```

Release overlay (operator secrets are mandatory; rendering fails closed):

```bash
POSTGRES_PASSWORD=... IDENTITY_DB_PASSWORD=... DRIVER_DB_PASSWORD=... \
TRIP_DB_PASSWORD=... MATCHING_DB_PASSWORD=... PAYMENT_DB_PASSWORD=... \
NOTIFICATION_DB_PASSWORD=... RABBITMQ_PASSWORD=... \
docker compose -f compose.yaml -f deploy/compose/compose.release.yaml config --quiet
```

The application images run with `ASPNETCORE_ENVIRONMENT=Production`; Trip
auto-migration is disabled in the release overlay, so migrations are an
explicit operator step.

## Clean-clone guide

Use a fresh clone or a clean worktree at the candidate commit. Run
`python scripts/security/init-local-auth.py`, then the Compose command above.
The exact-tree GitHub CI performs a fresh checkout, restore, Release build,
full test suite, Compose startup, security gate, core-runtime gate, payment
smoke and topology smoke.

## Evidence

- W14 G4/G5 accepted evidence: `evidence/team/w14-integration/`.
- W15 freeze/report evidence: `evidence/team/w15-integration/`.
- W16 release evidence: `evidence/team/w16-integration/`.
- Member reports: `docs/member-a-trip-saga-report.md`,
  `docs/driver-matching-report.md`, `docs/member-c-reliability-report.md`.
- Team report: `docs/team-technical-report.md`.

## Rehearsal status

- Automated technical rehearsals: B's real-provider harness executes three
  independent empty-volume trials plus shared complete/cancel/decline journeys;
  the integrated core-runtime gate executes completed, cancellation/void and
  terminal-decline scenarios with AUD-01..08. A and C provide labelled demo
  aids, not substitutes for authority proof.
- Human team rehearsals: **PENDING**. Automation reports
  `humanParticipation: NOT PERFORMED`.
- Oral defense drills and A/C peer sign-off: **PENDING**.

## Security and release controls

- Secret scan: PASS on the exact candidate; 55 real HTTP security checks and
  seven dedicated Identity negative cases pass in CI.
- Release overlay: renders with all eight explicit secrets; a secretless
  render fails closed with `must be set for the release overlay`.
- No credentials are written to evidence; `.env` and `secrets/` are ignored.
- No branch protection bypass, force-push or contributor-branch deletion is
  part of this release.

## Known limitations

- Human rehearsals, oral defense and peer sign-off cannot be produced by
  automation and remain final submission gates.
- The HA evidence covers the recorded quorum/broker scenario, not whole-system
  or multi-region HA.
- Performance results are measurements of the recorded single-host workloads,
  not production capacity claims.
- A's mock dashboard workflow and C's mock traffic generator are presentation
  aids; they are not end-to-end business correctness proof.
- The final tag/GitHub Release is not created by this document.

## Release checklist

- [x] Source freeze and member/team reports
- [x] Fail-closed release configuration and secret scan
- [x] Exact-tree CI on the final candidate
- [x] Automated technical rehearsals and integrated authority journeys
- [x] Evidence manifests and source anchors
- [ ] Three actual human team rehearsals
- [ ] Human oral defense and peer sign-off
- [ ] Project-lead authorization for `v1.0.0` tag/GitHub Release
