# Driver HTTP seam — freeze note (B-W02-08)

- **Status:** PROPOSED — needs A/C sign-off before W6.
- **Owner:** B (Matching side). Driver-side endpoints land in W6/W7.
- **Canonical source:** Doc 02 §12.5–12.6, §19.3–19.4, §19.6–19.7.

## Frozen (B proposal)

### Reserve — `POST internal/drivers/{driverId}/reservations`

Body: `{ "tripId": "<uuid>", "reservationId": "<uuid>" }`.
`reservationId` is stable per logical attempt across retry/reconcile/restart.

| HTTP | Meaning | Client maps to |
|---|---|---|
| 200/201 `{status: RESERVED, version}` | authoritative commit | `Reserved(version)` |
| 409 `{code}` | conclusive conflict (e.g. `DRIVER_NOT_AVAILABLE`) | `Rejected(code)` — must NOT trip breaker |
| 404 | driver unknown | `Rejected(DRIVER_NOT_FOUND)` |
| 5xx / timeout / connection drop | transport uncertainty | **throw** → caller classifies UNKNOWN, reconciles same id |

Rules: no generic retry (`02 §19`), no hedging (`02 §19.7`), 409 is not
infrastructure failure (`02 §19.4`), never assume success on unavailability
(`02 §19.6`).

### Reconcile — `GET internal/drivers/{driverId}/reservations/{reservationId}`

200 `{outcome}` where outcome ∈ `OWNED_BY_THIS_RESERVATION`,
`NOT_COMMITTED_OR_AVAILABLE`, `RESERVED_BY_OTHER`, `OFFLINE`, `ON_TRIP`,
`NOT_FOUND` (`02 §12.6`). Transport failure ⇒ caller treats as
`DEPENDENCY_UNAVAILABLE`. Unknown outcome string ⇒ throw (drift fails loud).

## Open questions for A/C

- [ ] A: confirm the Reserve route + body above, or supply the canonical one.
  (Canonical docs freeze only the reconcile GET, `02 §12.6` — the Reserve
  route here is B's proposal and must not be treated as frozen until signed.)
- [ ] A: exact 409 code set Driver guarantees (minimum: `DRIVER_NOT_AVAILABLE`).
- [ ] A: auth story for `/internal/*` (02 §3066: not unprotected public).
- [ ] C: attempt-timeout/total-deadline budgets vs `02 §20` (W6 freezes numbers).
- [ ] A: reconcile for a reservation whose driver row was since removed — `NOT_FOUND`?

## What this is NOT

Not reserve behavior, not UNKNOWN classification, not retry policy — W6/W7.
This directory is interface + shapes + mapping only.
