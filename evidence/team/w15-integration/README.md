# TEAM W15 integration evidence

Scope: freeze, report assembly and evidence-integrity record for the W15
release-candidate source integrated from Member A (PR #49), Member B (PR #62)
and Member C (PR #54) onto verified W14 main.

- Base main: `2b1b047b450074bd1d3af5a29e1f6f0e3ce8f07d`
  (tree `10e1480cec802fa50603b254e6f430324825550e`)
- Verified source commit: `733f071150687613da1c93e123882161de43e95f`
  (tree `774585d627c9a6b51deef1872f9a1a22a4b3f8fc`)
- This directory is an evidence-only commit: everything outside `evidence/`
  is unchanged from the verified source commit. The source-anchor verifier is
  therefore expected to pass against that commit.

## What was recovered

- A: two W15 report/evidence commits, patch-identical.
- B: six W15 freeze/report/evidence commits, patch-identical.
- C: three content commits whose non-CI files are byte-identical to the source
  commits; `.github/workflows/ci.yml` was reconciled onto newer W14 main
  instead of overwriting it. One C commit was already present in main and was
  skipped; one hygiene-script commit was already present and only its portable
  file-mode fixture was recovered from PR #56 commit `936a2c7`.
- No cumulative A/C history, W13/W14 ancestry or PR #56 W16 stack was imported.

See `source-mapping.json` for full SHAs and per-commit equivalence.

## Verification performed

Commands were run from the worktree root unless noted.

```bash
docker run --rm -v "$PWD:/work" -w /work mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -lc 'dotnet restore RideHailing.Distributed.slnx && \
  dotnet build RideHailing.Distributed.slnx -c Release --no-restore'

python scripts/security/scan-secrets.py
POSTGRES_PASSWORD=... IDENTITY_DB_PASSWORD=... DRIVER_DB_PASSWORD=... \
TRIP_DB_PASSWORD=... MATCHING_DB_PASSWORD=... PAYMENT_DB_PASSWORD=... \
NOTIFICATION_DB_PASSWORD=... RABBITMQ_PASSWORD=... \
  docker compose -f compose.yaml -f deploy/compose/compose.release.yaml config --quiet

env -u POSTGRES_PASSWORD -u IDENTITY_DB_PASSWORD -u DRIVER_DB_PASSWORD \
  -u TRIP_DB_PASSWORD -u MATCHING_DB_PASSWORD -u PAYMENT_DB_PASSWORD \
  -u NOTIFICATION_DB_PASSWORD -u RABBITMQ_PASSWORD \
  docker compose --env-file /dev/null \
  -f compose.yaml -f deploy/compose/compose.release.yaml config --quiet
# expected: non-zero with "must be set for the release overlay"

bash scripts/test/hygiene-check.sh --self-test
GITHUB_EVENT_BEFORE=origin/main bash scripts/test/hygiene-check.sh
bash scripts/test/verify-source-anchor.test.sh
python -m unittest discover -s scripts/perf/tests -p 'test_*.py'
python scripts/evidence/validate-driver-matching-claims.py
```

Results are curated in `test-output.txt` and
`final-state/integrity.json`. The Release build produced 0 errors and 14
analyzer warnings, matching the B source-bound freeze count. B's seven
manifest hashes were rechecked with canonical-LF bytes.

## What this bundle does not claim

- No full local Testcontainers regression rerun. The exact-tree GitHub CI
  `build-test` and `platform-smoke` jobs are the authoritative full-suite
  result for this candidate; CI also re-runs the security and runtime gates.
- No human peer clean clone. Independent automated verification is recorded;
  a separate member personally performing TEAM-W15-02 remains a course gate.
- No runtime HA acceptance from configuration rendering. W14's accepted,
  source-bound recovery evidence remains the HA authority.
- No production capacity or multi-region claim from historical measurements.

## Acceptance summary

`task-status.md` contains the full A/B/C/TEAM matrix and adjudication. The
source is technically frozen and verified; the outstanding items are the
human/process gates, not unverified runtime changes.
