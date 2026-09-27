# Notification Worker — Owner C

- Owner: C (reviewer A, backup B)
- Authority: none (non-authoritative delivery projection). Must NOT mutate Trip/Driver/Payment truth.
- DB: `notification_db` (role `notification_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3, 01 §11, 05C
- Bootstrap: template worker only. No subscriptions yet.
