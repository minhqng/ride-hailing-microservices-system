# Member C W1-W4 review - 2026-09-28

Scope: Doc 00 architecture/authority/failure model; Doc 01 Payment and cross-service invariants; Doc 02 sections 5-10, 15, 28 and topology/security contracts; Doc 03 CON/REL/SEC evidence requirements; Doc 04 weeks 1-4 and reviewer matrix; Doc 05C sections 20-23. Canonical docs were not rewritten to relax requirements.

## Findings and corrections

| Week | Finding | Correction / proof |
|---|---|---|
| W1 | Notes described REQUESTED/FAILED, inconsistent with the chosen model; ACK wording was wrong | Notes now distinguish PENDING/PROCESSING/UNKNOWN and explain ACK permits the broker to remove the delivery; invariant/evidence/reviewer map added |
| W2 | Collector explicitly deferred although assigned in W2; readiness always 200 | Collector pipelines, service resource identity, ASP.NET telemetry, bounded owned-DB readiness, Jaeger trace query |
| W2 | Stack existed without an executable infrastructure proof; stacked PRs did not trigger CI | CI targets weekly branches and PR bases; real Compose startup, own-DB positive controls and all 30 cross-role CONNECT denials |
| W2 | Docker restore omitted referenced projects; worker runtime lacked shared framework after instrumentation | Full source available at restore; ASP.NET runtime for instrumented worker; all 14 core/obs containers must be running |
| W2 | Bootstrap interpolated passwords into SQL | psql variables plus PostgreSQL format with literal/identifier escaping |
| W3 | UNKNOWN/PROCESSING/REQUIRES_REVIEW could enter another execution attempt | Only PENDING can begin; meaningful legal/illegal state tests |
| W3 | No state/effect tests; connection leak in W3; completed_at unmapped; invalid enum and omitted fields could enter processing | Connection disposal, terminal timestamp mapping, validation before writes, explicit required request fields, 26 Payment unit cases |
| W3 | Response could differ from authoritative PostgreSQL timestamp representation; caller cancellation could interrupt outcome persistence | Return reread stored row; bounded completion token independent of caller; HTTP/SQL/restart smoke for all 18 type/mode pairs |
| W4 | Bindings used queue-like routing names rather than catalog keys; command/projection queues absent | All 22 Doc 02 catalog routes covered by nine independent quorum queues; fan-out checked on real RabbitMQ |
| W4 | Envelope omitted aggregate/saga/operation identities; trace data mixed into payload; no persistence helper | Canonical V1 wire fields, separate transport trace headers, validation, persistent Rabbit properties, ten contract tests |
| W4 | Reused headers retained stale tracestate; topology/security tests absent | Clear stale headers; broker redeclare/restart, exact bindings, persistent routing and Production endpoint-isolation smoke |

## Reproduction

1. Build/test: `dotnet test RideHailing.Distributed.slnx -c Release` with .NET 10 SDK.
2. Disposable platform: `docker compose --profile obs up -d --build`.
3. `python scripts/test/platform-smoke.py`.
4. W3+: `python scripts/test/payment-smoke.py`.
5. W4: `python scripts/test/topology-smoke.py`.

The smoke scripts are intended for a disposable project: Payment smoke creates test rows; topology smoke publishes and consumes broker test messages. They do not prove later-week concurrency, Outbox, dedup, confirms/ACK recovery or HA. Windows review machine had no Docker; real infrastructure tests execute in GitHub Actions. CI results on the final commit, including logs, are linked from PRs #1-#4. Do not count bootstrap Assert.True tests in other projects as meaningful domain coverage. The Payment integration placeholder is explicitly skipped; actual PostgreSQL verification runs in the smoke script.

## Outstanding acceptance and scope

- A/B walkthrough, required independent review, actual teammate clean-clone rerun and GitHub Project board setup remain unconfirmed. Weekly PRs and a repository checklist do not prove these human/team tasks are done.
- W3 is a Development-only operation simulator. Same-key deduplication/fingerprint belongs to W5; interrupted PROCESSING/UNKNOWN reconciliation belongs to W7. No production idempotency or complete Payment workflow claim is made.
- Auth negative tests on public business APIs depend on the Identity/owner implementation; W4 validates simulator controls are absent in Production and DB isolation only.
- Local quorum declarations on one broker are not a high-availability proof. No RabbitMQ publisher/consumer business workflow runs in W4; W8 owns confirms/manual ACK.
- An existing development broker may retain obsolete bindings from older code. Use a fresh disposable Compose project for reproducible review; do not delete shared data to repair a test environment.

Review is complete only when actual checks and peer acceptance support each claim. See the week-specific notes and PR check results for the current status.
