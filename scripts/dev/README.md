# Local stack lifecycle

The supported local workflow is Docker Compose from the repository root. There
are deliberately no `up`/`down`/`reset` wrappers: a three-line wrapper that ran
`docker compose down -v` could destroy a shared project's volumes without the
operator noticing which project it targeted.

## Isolate the project first

`compose.yaml` declares `name: ride-hailing`, so two checkouts on one host share
that Compose project unless you override it. Select a disposable name before
any up/down command:

```bash
export COMPOSE_PROJECT_NAME=ride-hailing-$(id -un)-dev
```

## Commands

```bash
cp .env.example .env                     # once; .env is gitignored
python3 scripts/security/init-local-auth.py
docker compose up -d --build             # start (builds images)
docker compose down                      # stop; volumes are kept
```

Volume deletion is a separate, deliberate act. Confirm the resolved project
name and that the data is disposable first:

```bash
docker compose config --format json | grep -m1 '"name"'
docker compose down -v                   # deletes THIS project's volumes
```

Never run a destructive command against the default `ride-hailing` project or
any project that holds data you need. The release overlay
(`deploy/compose/compose.release.yaml`) follows the same rule: export a unique
`COMPOSE_PROJECT_NAME` before rendering or starting it.

## Data conventions

- PostgreSQL init creates roles and empty owned databases. There is no business
  seed.
- Owner-specific seeds, if added later, live in `scripts/dev/seeds/<service>/`
  with explicit versions and documented invocation, using only the owning
  service credentials.
- Use stable fixture IDs and idempotent inserts. No cross-service SQL writes.
- Test data must be isolated and identified in test output. The Payment smoke
  script creates test data; it is not a production seed.
- Do not commit passwords, tokens or personal data.
