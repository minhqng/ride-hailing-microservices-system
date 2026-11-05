# Reproduce the DS03 closure evidence

Prerequisites: Docker engine (Linux containers), .NET SDK 10, Python 3,
Git Bash. The release smoke creates disposable projects and removes them;
never point it at a shared project.

## 1. Prove the source anchor

From a clean checkout of this evidence commit (or its verified source commit):

```bash
git status --short
scripts/test/verify-source-anchor.sh f2ff224f397c76d66af20358fe7615d6aba186ad caac264b93c1d5b441ac1b1d0be87253162ccbc0
```

Everything outside `evidence/` must be identical to the verified source
commit. A dirty source tree is drift, not a new result.

## 2. Full .NET regression

```bash
dotnet tool restore
dotnet restore RideHailing.Distributed.slnx
dotnet build RideHailing.Distributed.slnx -c Release --no-restore
TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet test RideHailing.Distributed.slnx -c Release --no-build \
  --logger trx --results-directory TestResults/full
python scripts/test/assert-trx.py \
  --results-dir TestResults/full --min-passed 1200 --min-files 13 \
  --max-skipped 1 \
  --allowed-skip RideHailing.IdentityService.IntegrationTests.AuthNegativeTests.RejectedRequestsDoNotMutateAuthoritativeState \
  --required-suites-file scripts/test/required-suites.txt
```

Expected on the verified source: 13 executed suites, 1,341 passed, 0 failed,
1 known Identity opt-in skip, TRX verifier PASS.

## 3. Real Production release smoke (Scenarios A–D)

```bash
export PATH="$PATH:${DOTNET_ROOT:-$HOME/.dotnet}"
RELEASE_SMOKE_REPLACE_DEV_ENV=1 python scripts/test/release-smoke.py
```

The script renders the release overlay with disposable secrets, applies Trip
EF migrations externally, starts the Production Compose topology, runs:

- A: Driver rejects a reserved Trip; exact release, terminal result, no
  payment, new ride allowed, Rider notification.
- B: accept → authorize → start → complete → capture; both roles read their
  inboxes.
- C: Notification worker stopped during the ride; Trip/Driver/Payment finish;
  restart delivers queued notifications exactly once.
- D: reject commit, Trip process stop/restart, Outbox/reconciliation resumes,
  exact replay with one logical release.

Expected: `PASS: Production release overlay runtime smoke`, report at
`plans/reports/release-smoke.json` with source
`f2ff224f397c76d66af20358fe7615d6aba186ad` and 19 checks. The script tears down
its Compose project and preserves unrelated containers.

## 4. Hygiene and security boundaries

```bash
scripts/test/hygiene-check.sh --self-test
scripts/test/hygiene-check.sh origin/main
python scripts/test/security-gate.py --identity-tests   # after platform-smoke stack is up
```

The DS03 inbox security assertions are in the Notification integration suite
(real signed JWTs) and in `scripts/test/security-gate.py`
(`notification-*` cases).
