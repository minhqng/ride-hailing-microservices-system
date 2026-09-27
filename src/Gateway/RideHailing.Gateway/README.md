# Gateway — Owner C

Edge routing only. No domain state.

- Owner: C (reviewer A, backup B)
- DB: none
- Deps (future): `Services__IdentityBaseUrl`, `Services__DriverBaseUrl`, `Services__TripBaseUrl`, `Services__MatchingBaseUrl`, `Services__PaymentBaseUrl`
- Read: Docs 00 §4-6, 02 §2-4, 05C
- Bootstrap: skeleton + `/health/*`, `/info`. No YARP routing yet (W8).
- NOT implemented: routing, auth forwarding, rate controls.
