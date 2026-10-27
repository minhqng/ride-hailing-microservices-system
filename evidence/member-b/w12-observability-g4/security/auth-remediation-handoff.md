# SEC-AUTH-01..04 remediation handoff

**Status: OPEN — G4 release blocker.** This is a confirmed vulnerability record,
not a passing negative test. Overall G4 remains **FAIL/BLOCKED** until these
findings are remediated and retested.

| ID | Severity | Confirmed finding | Primary owner |
|---|---|---|---|
| SEC-AUTH-01 | **CRITICAL** | Unauthenticated HTTP callers can mutate Driver authoritative state and commit Outbox intents (online/offline/reserve/release/create/location). | Identity/token contract: A; Gateway boundary: C; Driver/Matching enforcement: B; integration approval: lead |
| SEC-AUTH-02 | **HIGH** | Actor-ID spoofing: route `{id}` is the only actor; `X-Driver-Id`, `X-Role` and an unsigned Bearer are ignored, not rejected. | A (claims) + B (enforcement) |
| SEC-AUTH-03 | **HIGH** | Internal Driver/Matching routes are directly reachable on Compose-published host ports with no service identity. | C (Gateway/network) + B (service guard) + lead |
| SEC-AUTH-04 | **MEDIUM** | No object-level authorization binds an authenticated Driver/User to the requested Driver or reservation. | A + B after the shared contract |

Supporting evidence: `security/inventory.md`, `security/http-probes.txt`; real
Kestrel/PostgreSQL/Outbox tests `DriverAuthorizationExposureTests` (2/2 passed as
exposure reproduction). The denied-without-mutation oracle is not claimed.

## Required shared contract (do not invent a B-local substitute)

1. **Authenticated actor identity / trusted principal claims.**
   A's Identity service issues a signed, short-lived principal (actor id, roles,
   issuer, audience, expiry, unique token id). Gateway validates it and forwards
   only trusted claims. Services derive the actor from validated claims, never
   from request bodies or unsigned headers.
2. **Service-to-service authentication.**
   Matching→Driver reserve/reconcile and Trip→Driver release must authenticate a
   service principal (client credentials, mTLS, or an equally approved signed
   service token) with an explicit operation permission.
3. **Driver ownership checks.**
   Driver-bound routes require `actor == driverId` (or an approved admin/service
   principal). Object-level checks cover trip/reservation ownership; the chosen
   403-vs-404 disclosure policy must be consistent with SEC-03.
4. **Internal endpoint exposure / network restriction.**
   Internal reserve/release/reconcile and Matching round-trigger routes should be
   bound to an internal listener or Compose network and removed from host
   publication; Gateway exposes only intended public routes.
5. **Authorized Matching reserve and Trip release permissions.**
   Reserve requires `matching:reserve`; release requires `trip:release`; the
   domain token (exact trip/reservation identity) remains required — authz does
   not replace OCC or fencing.
6. **Reject-without-mutation test matrix.**
   Missing / invalid-signature / expired / malformed token; wrong role; wrong
   actor; wrong object; wrong service principal; unsigned spoofed headers.
   Assert the intended 401/403, unchanged Driver state/version, zero new Outbox
   intents, zero accepted location publications and no new reservation.
7. **RabbitMQ command-consumer compatibility.**
   Consumers authenticate with broker credentials; envelope/principal metadata
   identifies the service actor. Do not invent unsigned-header trust, a
   development JWT key, or a fake Identity implementation. Existing command
   payloads, receipt/poison semantics and duplicate-delivery behavior must stay
   unchanged.

## B implementation boundary

B will implement Driver/Matching authentication and authorization enforcement
only after the shared principal contract and Gateway behavior are approved and
available. The minimal B change is middleware + policy checks on the existing
routes plus the reject-without-mutation matrix; no service-to-service protocol,
Identity implementation or Gateway topology is invented in B.

**Exit criteria:** SEC-AUTH-01..04 closed with real-boundary tests, the G4
matrix re-run against the remediation source, and an independent peer rerun.
Until then the honest status is `HANDOFF DELIVERED / HUMAN REVIEW PENDING /
PEER RERUN PENDING`.
