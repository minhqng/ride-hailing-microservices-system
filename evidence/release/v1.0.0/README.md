# Final release evidence — v1.0.0

Scope: verification of the distributed ride-hailing platform at the `v1.0.0`
release. This bundle records the final regression, the Production overlay
smoke, static/gate results and the report artifacts.

## Source anchor

- Base `main` commit: `da85ca3aaa7ef61820bd9bef585aac1abdd96e33`
  (tree `ca4be8e1da77aa7e90593265a35531d89ea1fe77`).
- Verified source commit: `5eba1e4c7aceb7ea51e4a5a82dc583bec5f22011`
  (tree `06d9e06de97752c18739cfa6051aa9e3fce75900`).
- This directory is an evidence-only addition. Runtime source and
  documentation are unchanged from the verified source commit.
- The annotated `v1.0.0` tag points at the final `main` commit whose tree
  contains this bundle; the exact commit SHA is recorded in the tag object,
  the GitHub Release notes and the release report.

## Verification

- Full .NET regression: **1,341 passed, 0 failed, 1 documented opt-in skip**
  across 13 executable suites. TRX gate PASS.
- Production release overlay smoke: **19/19 checks PASS**, 11 external Trip
  migrations, 12 exchanges / 19 queues, completed + cancelled/void + rejected
  journeys, notification-outage isolation and reject-commit crash replay.
- Secret scan: PASS on 1,340 tracked paths.
- Hygiene self-test: 24/24 pattern verdicts; source-anchor fixtures: 12/12.
- Build: 0 errors, 18 analyzer warnings after the cleanup.

The authoritative exact-tree run for the tagged tree is the GitHub Actions
`ci` workflow on the release pull request (hygiene, compose-config,
build-test, platform-smoke and release-smoke); its run id is recorded in the
GitHub Release notes. The local smoke summary in `final-state/` is supporting
evidence and is labelled as such.

## Reproduction

```bash
git clone https://github.com/minhqng/ride-hailing-microservices-system.git
cd ride-hailing-microservices-system
git checkout v1.0.0
python3 scripts/security/init-local-auth.py
bash scripts/test/quick.sh
python3 scripts/test/release-smoke.py
python3 scripts/security/scan-secrets.py
```

`quick.sh` runs the 13-suite regression in the pinned .NET 10 SDK container
and applies the TRX gate. `release-smoke.py` uses an isolated Compose project
and disposable secrets; it tears down only its own resources.

## What this bundle does not claim

- No human team rehearsal, oral defense or peer sign-off.
- No real payment network; the provider is simulated with durable effects.
- No multi-region or whole-system HA; HA evidence covers the recorded
  single-host quorum topology.
- No production capacity claim; performance results are single-host
  measurements.
