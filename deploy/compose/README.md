# Compose profiles

`core` lives in root `compose.yaml`. `deploy/compose/compose.release.yaml` is
the Production overlay; `deploy/compose/compose.ha.yaml` adds the explicit
broker-HA profile. All images are pinned by multi-architecture manifest digest;
see [IMAGES.md](IMAGES.md) for the table and update procedure.

## Release overlay

- Requires twelve explicit values: eight database/broker passwords plus
  `AUTH_PUBLIC_KEY_BASE64`, `AUTH_PRIVATE_KEY_BASE64`, `AUTH_MATCHING_SECRET`
  and `AUTH_TRIP_SECRET`. Compose interpolation fails closed when one is
  absent.
- Forces `ASPNETCORE_ENVIRONMENT=Production` for the HTTP services,
  `DOTNET_ENVIRONMENT=Production` for the worker, real Trip messaging
  (`TripMessaging__Enabled=true`) and external Trip migrations
  (`TripDatabase__AutoMigrate=false`).
- Payment's Development simulator routes are absent in Production. The
  decline path is covered by the Payment integration suite.

Use an isolated project (`COMPOSE_PROJECT_NAME=ride-release-<unique>`) and run
`python3 scripts/test/release-smoke.py` for the executable procedure, which
also covers external EF migrations, readiness, real journeys, authority reads
and isolated teardown. See `docs/release.md` for the operator steps.
