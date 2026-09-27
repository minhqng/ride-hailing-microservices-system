# Identity Service — Shared / A-supported

- Owner: Shared/A (reviewer C, backup B)
- Authority: user/account, role, auth baseline. Does NOT own Driver/Trip/Payment.
- DB: `identity_db` (role `identity_app`)
- Deps: `ConnectionStrings__Postgres`
- Read: Docs 00 §3, 01 §3, 05A
- Bootstrap: skeleton only. No registration/login yet (W3+).
