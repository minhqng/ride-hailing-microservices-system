# Reset and seed convention

- Select a disposable COMPOSE_PROJECT_NAME. reset.sh destroys only that project's volumes; never run it on shared data.
- PostgreSQL init creates roles and empty owned databases. W2 has no business seed.
- Later owner seeds live in scripts/dev/seeds/<service>/ with explicit versions and documented invocation, using only the owning service credentials.
- Use stable fixture IDs and idempotent inserts. No cross-service SQL writes.
- Test data must be isolated and identified in test output. W3 Payment smoke creates test data; it is not a production seed.
- Do not commit passwords, tokens or personal data.
