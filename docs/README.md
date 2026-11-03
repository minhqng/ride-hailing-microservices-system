# Docs index

Canonical specs (do not rewrite; code wins only for exact patch/config after approved freeze):

- `00-project-scope-architecture.md` → architecture/scope
- `01-domain-correctness-contract.md` → correctness/invariants
- `02-distributed-workflow-engineering.md` → distributed workflow/reliability
- `03-verification-experiments-demo.md` → verification/experiments
- `04-team-execution-plan-v2.md` → team execution
- `05A-member-a-trip-workflow.md` → Member A playbook
- `05B-member-b-driver-matching.md` → Member B playbook
- `05C-member-c-payment-messaging-platform.md` → Member C playbook
- `06-history-attribution-correction.md` → W09/W10 authorship correction and credit-allocation record

Reading order: 00 → 01 → 02 → 03 → 04 → 05A/B/C. Priority: 00–03 over 04 over 05.


Runtime guides (implementation usage; canonical specs above remain authoritative):

- [Team technical report](team-technical-report.md): W15 freeze synthesis,
  architecture, methods, verification and contribution traceability.
- [Release candidate](release-candidate.md): W16 candidate identity, changelog,
  build/deploy, evidence, rehearsal status and release checklist.
- [Trip, Outbox and Saga report](member-a-trip-saga-report.md): Member A
  lifecycle, Outbox crash windows, compensation and evidence scope.
- [Driver/Matching technical report](driver-matching-report.md): authority,
  OCC, Redis, durable UNKNOWN recovery, proof diagrams and measured-claim limits.
- [Messaging and Payment reliability report](member-c-reliability-report.md):
  Member C confirms, ACK-after-commit, deduplication, Outbox relay, DLQ,
  Payment idempotency, observability and HA claim boundaries.
- [Driver/Matching demonstration](driver-matching-demo.md): disposable real
  rehearsals, A/C handoff, fallback and final release authority.
- [Driver/Matching defense workbook](driver-matching-defense.md): all45
  canonical questions, exact proof references and timed human practice drill.

- [Messaging runtime](messaging-runtime.md): publisher/consumer lifecycle, confirms, ACK and retry ownership.
- [Payment simulator](../src/Services/Payment/RideHailing.PaymentService/README.md): durable replay, reconciliation and verification commands.
