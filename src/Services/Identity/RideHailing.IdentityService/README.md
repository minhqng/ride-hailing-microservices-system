# Identity Service — Shared / A-supported

- Owner: Shared/A (reviewer C, backup B)
- Authority: user/account, role, auth baseline. Does NOT own Driver/Trip/Payment.
- DB: `identity_db` (role `identity_app`)
- Deps: `ConnectionStrings__Postgres`, RSA validation/signing keys and service credentials described in [authentication runtime](../../../../docs/authentication-runtime.md).
- Read: Docs 00 §3, 01 §3, 05A
- Registration/login use the owned PostgreSQL account store and ASP.NET Core password hashing. Signed short-lived user and service grants are validated independently by Gateway and receiving services.
