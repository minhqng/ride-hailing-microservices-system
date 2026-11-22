# Final release evidence — v1.0.1

Scope: verification of the distributed ride-hailing platform at the `v1.0.1`
patch release. This bundle records the full regression, the live demo run, the
Production overlay smoke, the static/repository gates and the submission
artifacts. The `v1.0.0` bundle remains the immutable historical record.

## Source anchor

- Base: `v1.0.0` commit `74d5b9ffe0877e3fad9bffa80194812cd85ba533`.
- Verified source: `b8467c971cc7eb8efffeacf4c762b80638607da7` (tree `7851fd467c8a5a95dfdd7c2d18ba696e8fd07555`).
- This directory is an evidence-only addition. Runtime source and documents
  are unchanged from the verified source commit.
- The annotated `v1.0.1` tag points at the final commit whose tree contains
  this bundle; its exact SHA is recorded in the tag object.

## Verification

- Full .NET regression: **1,344 passed, 0 failed, 1 documented opt-in skip**
  across 13 executable suites; TRX gate PASS; build 0 errors / 0 warnings.
- Live demo on a clean, rebuilt development stack: Part C (13 steps), D.1–D.8,
  D.11, D.12 and a two-candidate matching probe. Final invariant audit:
  `cleanup_pending = 0`, `non_published_outbox = 0`,
  `reserved_rounds_with_unresolved = 0`, `duplicate_notifications = 0`.
- Production release overlay smoke: `**19/19 checks PASS**`.
- Static gates: secret scan PASS on 576 tracked paths; hygiene self-test
  24/24; source-anchor fixtures 12/12; development and release Compose renders
  PASS, release overlay fails closed without secrets.
- Platform, security, core-runtime, payment and topology gates: PASS on the
  dedicated `ride-test-v101` Compose project.

## Fixes verified in this release

1. A matching round that commits one reservation closes all unselected `READY`
   attempts as `CANCELLED` (`NOT_SELECTED`), including when cancellation
   arrives after the round settled. No `RESERVED` round has an unresolved
   attempt and `CancelMatching` confirmation converges.
2. Cancelling a `DRIVER_RESERVED` ride no longer creates a `ReconcilePayment`
   obligation: authorization is only requested by accept, so no Payment effect
   exists at that state. A cancel at `DRIVER_ACCEPTED` or `READY_TO_START`
   still reconciles the in-flight authorization.

## Reproduction

```bash
git checkout v1.0.1
python3 scripts/security/init-local-auth.py
bash scripts/test/quick.sh
python3 scripts/test/release-smoke.py
python3 scripts/security/scan-secrets.py
```

For the live demo, follow [docs/demo.md](../../../docs/demo.md) from a clean stack:

```bash
COMPOSE_PROJECT_NAME=ride-demo docker compose --profile obs down -v
OBS_PROMETHEUS_PORT=19090 COMPOSE_PROJECT_NAME=ride-demo \
  docker compose --profile obs up -d --build
```

`quick.sh` runs the 13-suite regression in the pinned .NET 10 SDK container
and applies the TRX gate. `release-smoke.py` uses an isolated Compose project
and disposable secrets and tears down only its own resources.

## What this bundle does not claim

- No human team rehearsal, oral defense or peer sign-off.
- No real payment network; the provider is simulated with durable effects.
- No multi-region or whole-system HA; HA evidence covers a single-host quorum.
- No production capacity claim; performance results are single-host data.
- Raw run logs remain local and are not committed; this bundle contains curated
  counts, verdicts and final-state assertions only.
