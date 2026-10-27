# B-W12-05 — HTTP authorization and actor-spoofing inventory

Status: **OPEN — `SYSTEM_AUTH_PENDING`**. This is not an authorization PASS.
The Driver service currently has no authenticated principal, so every
unauthorized mutation below was independently reproduced over real HTTP.

## 1. Trust boundaries observed

| Boundary | Current state | Evidence |
|---|---|---|
| Identity service | Skeleton: `/health/live`, `/health/ready`, `/info` only. No registration, login, token issuance or validation. | `src/Services/Identity/RideHailing.IdentityService/Program.cs:1-17`; its README states the same. |
| Gateway | Skeleton: health/info only. No routing, no auth forwarding, no token validation. | `src/Gateway/RideHailing.Gateway/Program.cs:1-17`; its README states the same. |
| Driver service | No `AddAuthentication`/`UseAuthentication`/`UseAuthorization` anywhere in `src`. Route `{id}` is the only actor identity. | `grep -R "AddAuthentication\|UseAuthentication\|UseAuthorization\|RequireAuthorization" src` → no matches. |
| Matching service | Same: no authentication middleware; `/internal/matching/rounds` is callable directly. | `src/Services/Matching/RideHailing.MatchingService/Program.cs:159-177`. |
| Compose host exposure | Driver `8102:8080`, Matching `8104:8080`, Identity `8101:8080`, Gateway `8080:8080` are published on the host. Internal routes are not network-isolated from host callers. | `compose.yaml:107,120,137,177`. |
| W11 fault controls | Test-only and fail-closed: mapped only in the explicit `Testing` environment and require `X-Fault-Control-Token`; absent in Production. | `src/Services/Driver/RideHailing.DriverService/Api/DriverFaultEndpoints.cs:17-52`; `src/Services/Matching/RideHailing.MatchingService/Api/MatchingTestEndpoints.cs:18-60`; `Program.cs` guards. |

## 2. Driver route inventory

| Route | Handler | AuthN | AuthZ / ownership | Intended caller |
|---|---|---|---|---|
| `POST /drivers` | `DriverEndpoints.cs:16` | none | `userId` is caller-supplied; no binding to a principal | Admin/Driver onboarding |
| `GET /drivers/{id}` | `DriverEndpoints.cs:54` | none | any caller may read any Driver | Driver/Matching/internal |
| `POST /drivers/{id}/online` | `DriverEndpoints.cs:64` | none | route id only; no Driver principal | Driver actor |
| `POST /drivers/{id}/offline` | `DriverEndpoints.cs:97` | none | route id only | Driver actor |
| `POST /drivers/{id}/release` | `DriverEndpoints.cs:132` | none | exact `tripId+reservationId` token only | Trip/Matching internal |
| `GET /internal/drivers/{id}/reservations/{reservationId}` | `DriverEndpoints.cs:166` | none | read-only truth query, but leaks reservation state | Matching internal |
| `POST /drivers/{id}/reserve` | `DriverEndpoints.cs:202` | none | exact reservation identity + OCC version | Matching internal |
| `POST /drivers/{id}/location` | `DriverEndpoints.cs:259` | none | route id only; no device/Driver principal | Driver device |
| `GET/POST /internal/test/faults*` | `DriverFaultEndpoints.cs:17,22` | control token | Testing-only, fail-closed | Test harness only |

Matching routes: `POST /internal/matching/rounds` (`MatchingEndpoints.cs:17`)
has no authentication; `POST /internal/test/matching/reserve` and the broker
test trigger (`MatchingTestEndpoints.cs:18,56`) are Testing-only with a
control token.

Exercised over real HTTP by W12: Driver create, read, online, offline,
reserve, release, internal reservation read and location. Not exercised:
`POST /internal/matching/rounds` — it has the same absence of middleware, but
exercising it needs a Matching host plus PostgreSQL/Redis; it remains a
reviewed finding, not a tested one.

## 3. Real HTTP exposure results

Executed by `DriverAuthorizationExposureTests` (real Kestrel, real PostgreSQL,
real Outbox; no fake identity provider, no trusted header shortcut):

| Case | Observed result | Business effect |
|---|---|---|
| Unauthenticated `POST /drivers/{id}/online` | **200 OK** | Driver version 1→2, status `AVAILABLE`, `DriverAvailableV1` Outbox intent committed |
| Unauthenticated `POST /drivers` / `GET /drivers/{id}` | **201 Created / 200 OK** | Arbitrary `userId` creates a Driver; any caller reads it |
| Unauthenticated `POST /drivers/{id}/offline` | **200 OK** | Driver `OFFLINE`, `DriverOfflineV1` Outbox intent committed |
| Spoofed `X-Driver-Id: A`, `X-Role: driver`, unsigned `Authorization: Bearer` mutating Driver B | **200 OK** | Driver B mutated; A's claimed identity is ignored, not enforced |
| `X-Role: rider` performing Driver-only online | **200 OK** | Driver mutated; no function-level boundary |
| Unauthenticated internal `POST /reserve` | **200 OK** | Driver `RESERVED`, exact reservation stored, `DriverReservedV1` Outbox intent committed |
| Unauthenticated internal `GET /internal/.../reservations/{R}` | **200 OK** | Reservation truth disclosed |
| Unauthenticated internal `POST /release` | **200 OK** | Driver back to `AVAILABLE`, `DriverReservationReleasedV1` Outbox intent committed |
| Unauthenticated `POST /drivers/{id}/location` | **202 Accepted** | Sample accepted and `DriverLocationAcceptedV1` published; covered by the location suite baseline |

Because **none** of these requests is denied, the requested denial oracle
("no state/version change, no Outbox intent, no accepted location event") is
not vacuous-positive here: the opposite outcome is observed and recorded. The
only denials in W12 are invalid-payload/oversize denials, which do not mutate
(see `security/http-probes.txt` and `../test-output/`).

## 4. Findings and ownership

| ID | Severity | Finding | Owner |
|---|---|---|---|
| SEC-AUTH-01 | **Critical** | Driver public and internal routes have no authentication; any host caller can mutate Driver authority and create Outbox intents. | Identity/token contract: Shared/A; Gateway boundary: C; service-side enforcement after contract: B; integration approval: lead |
| SEC-AUTH-02 | **High** | Actor-ID spoofing is unchecked. Route `{id}` is the only actor; `X-Driver-Id`, `X-Role`, and an unsigned Bearer are ignored rather than rejected. A caller can mutate any Driver. | Same as SEC-AUTH-01 |
| SEC-AUTH-03 | **High** | Matching internal reserve/reconcile routes and Driver internal reserve/release/reconcile are reachable directly on Compose-published host ports with no service identity. | C (Gateway/network) + B (service-side guard once contract exists) + lead |
| SEC-AUTH-04 | Medium | No object-level authorization: nothing binds an authenticated Driver/User to the requested Driver or reservation. | Shared/A + B after contract |
| SEC-07-01 | Medium (partial) | Driver now rejects request bodies over 64 KiB with HTTP 413 before binding (`DriverIngressLimits.cs`), but no rate limiting/concurrency limit exists at the Driver boundary. | B (body bound) + C/Gateway (rate limit) |
| SEC-08 | Info | DB credential isolation is covered by the existing `DriverDbOwnershipTests`; W12 did not change it. | B |
| SEC-09 | **PASS** | No tracked `.env`, no private-key/cloud-token/API-key patterns, no JWT signing-key literal; `.env.example` contains placeholders only. Compose dev defaults remain non-production values. Raw scan: `security/secret-scan.txt`. | B verified; lead owns production secret injection |

`SYSTEM_AUTH_PENDING` means exactly this: there is no usable authenticated
principal contract in the repository, so B did not invent JWT signing keys,
trust unsigned headers, or build a fake identity provider. Closing
SEC-AUTH-01..04 requires a shared Identity/Gateway contract plus a lead-approved
integration change; B owns the Driver/Matching enforcement once that contract
exists.
