# Gateway

Edge routing only. No domain state.
- DB: none
- Deps: `Services__IdentityBaseUrl`, `Services__DriverBaseUrl`, `Services__TripBaseUrl`,
  `Services__NotificationBaseUrl` (Matching and Payment are not exposed at the edge)

## What it does

- YARP reverse proxy with routes loaded from memory: `/auth/register` and
  `/auth/login` (anonymous), `/trips/**`, `/drivers`, `/drivers/{id}`,
  `/drivers/{id}/online|offline|location`, `/notifications/**`.
- JWT authentication via `AddRideAuthentication`; every non-anonymous route
  carries an `AuthorizationPolicy`, so an unauthenticated call is rejected at the
  edge with 401 and a wrong-role call with 403.
- Strips client-supplied `X-Driver-Id`, `X-Rider-Id`, `X-User-Id` and `X-Role`
  on every route, so identity can only come from the validated token.
- Bounds the request body to 64 KB at Kestrel.
- `/health/live`, `/health/ready` and `/info`. Readiness is process-only: the
  gateway owns no database.

## What it deliberately does not do

- No domain state, no business rules, no database.
- No edge rate limiting. Ingress limits are owned by the services that own the
  resource (for example `DriverRateLimitPolicy` for location updates), so a
  limit cannot be bypassed by calling a service directly on the Compose network.
- Matching and Payment are internal: they are reached by Trip and by each other,
  never by a client through the gateway.
