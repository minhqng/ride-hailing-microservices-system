# A-W12 F1 — Trip HTTP authorization boundary review (2026-10-26)

Independent review of whether A-owned HTTP operations are actually protected
against unauthorized actors and cross-Rider access, or whether the W12
negative tests only prove domain validation.

## Endpoint inventory (complete, verified in source)

| Route | Method | Identity gate | Role gate | Object-level gate |
| --- | --- | --- | --- | --- |
| `/trips` | POST | `RiderIdentity.TryExtractRiderId` → else 401 | `IsRider` → else 403 | rider derived ONLY from authenticated claims |
| `/trips/{tripId:guid}` | GET | 401 | 403 | `trip.RiderId != riderId` → 404 (no disclosure, SEC-03) |
| `/trips/{tripId:guid}/cancel` | POST | 401 | 403 | ownership inside `CancelRideHandler` → shared NotFound (SEC-03) |
| `/health/live`, `/health/ready`, `/info` | GET | none (liveness/config info only; no business data, no mutation) | — | — |

There is NO HTTP surface for internal authority events (Driver/Matching/
Payment facts arrive exclusively through broker consumers) and NO faultpoint
HTTP surface (config + non-Production environment only; Production enable
fails startup — W11 tests).

## Authentication vs authorization (the actual boundary)

- **Authentication (who is calling) is NOT implemented anywhere yet** — by
  design it is C-owned (Identity service and Gateway are both skeletons:
  Identity has health endpoints only; Gateway forwards nothing). Trip
  registers NO authentication middleware and issues/validates no tokens.
- Consequently, in every current deployment, `HttpContext.User` is
  unauthenticated, and **the claims boundary fails CLOSED**: without an
  authenticated principal with resolvable single-valued Guid identity
  claims, every rider operation returns 401 before any handler runs. There
  is no fallback-to-trusted path; malformed/conflicting claim values fail
  closed (proven by the W4 HTTP suites).
- **Authorization given an authenticated principal is enforced by Trip and
  verified**: role check (Rider), object-level ownership at the resource
  boundary (Trip row comparison, missing and not-owned share 404), actor
  identity taken only from claims (body/route actor fields proven inert).

## Deployment exposure fact

`compose.yaml` publishes every service directly to the host (Trip on
8103); the Gateway is not yet in the request path. This does NOT create an
A-owned vulnerability today because of the fail-closed claims boundary
above: a direct unauthenticated caller cannot read, create or mutate any
Trip/Saga/Outbox state. The residual platform dependency is that the
eventual authentication middleware (C) is what turns the claims boundary
into a full authentication boundary.

## Adversarial case mapping (existing executable proofs)

| # | Case | Result | Oracle |
| --- | --- | --- | --- |
| 1 | Rider A reads Rider B's Trip | 404, no disclosure | `TripLifecycleHttpTests.GetTrip_OtherRidersTrip_Returns404_WithoutDisclosureOrMutation` |
| 2 | Rider A cancels Rider B's Trip | 404 (shared NotFound), no mutation | `TripLifecycleHttpTests.CancelRide_OtherRidersTrip_Returns404_WithoutSideEffects` |
| 3 | Spoofed riderId/driverId/actor in body | inert; ownership stays with claims | `TripW12AuthzNegativeTests.CreateRide_BodyActorFieldsAreInert_OwnershipStaysWithClaims` |
| 4 | Unauthenticated state-changing call | 401 + zero rows | `CreateRideHttpTests.ConflictingNameIdentifier_Rejects401_WithoutSideEffect` asserts 0 trips and 0 idempotency rows |
| 5 | Normal user invokes internal authority operation | no such HTTP surface exists (inventory above) | endpoint inventory + `ApiDtos_DoNot_ExposeForbiddenMemberNames` |
| 6 | Rejected request creates Trip/Saga/receipt/Outbox effect | none | `TripW12AuthzNegativeTests` terminal-cancel cases assert durable state+version unchanged and 0 outbox rows |
| 7 | Malformed authorization context falls back to trusted identity | fails closed 401 | CreateRideHttpTests malformed/conflicting claim cases (5+ lanes) |
| 8 | Identity fixture skip hides an entry point | skip is visible and explicit | `RIDE_AUTH_FIXTURE`-gated xunit Skip, reported in every test-output record |

## Verdict

**No A-owned production authorization defect.** Authorization (role +
object-level, given an authenticated principal) is enforced at the Trip
boundary and proven with durable oracles; the boundary fails closed for
unauthenticated traffic. The open item is **authentication itself**
(ISSUANCE/VALIDATION of principals), which is C-owned (Identity/Gateway)
and remains SYSTEM_AUTH_PENDING in the G4 matrix — it is a real dependency,
not a harmless one: until it lands, the platform cannot have real Rider
traffic at all (fail-closed), and when it lands, Trip's existing claims
consumption becomes the enforcement point without Trip changes.
