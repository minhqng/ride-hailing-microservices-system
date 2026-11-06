# Documentation index

All repository documentation is organized into four groups. The canonical
contracts are authoritative for correctness; implementation playbooks record
how the work was executed; runtime guides describe day-to-day operation; the
release and provenance records preserve the final audit trail.

## 1. Canonical contracts (authoritative)

| Document | Content |
|---|---|
| [00-project-scope-architecture.md](00-project-scope-architecture.md) | Scope, service boundaries, architecture and design rationale |
| [01-domain-correctness-contract.md](01-domain-correctness-contract.md) | Domain invariants, authority boundaries and transition rules |
| [02-distributed-workflow-engineering.md](02-distributed-workflow-engineering.md) | Messaging, Outbox, Saga, idempotency, retry and recovery rules |
| [03-verification-experiments-demo.md](03-verification-experiments-demo.md) | Verification method, required scenarios, evidence hierarchy and demo contract |

Reading order: 00 → 01 → 02 → 03. If an implementation detail and a contract
disagree, treat the contract as the requirement and record the discrepancy in
the issue tracker; do not silently reinterpret the invariant.

## 2. Implementation playbooks (historical execution, still authoritative for invariants)

| Document | Content |
|---|---|
| [04-team-execution-plan-v2.md](04-team-execution-plan-v2.md) | Team ownership model, execution rules and release section |
| [05A-member-a-trip-workflow.md](05A-member-a-trip-workflow.md) | Trip/Booking, Saga, Outbox and compensation implementation notes |
| [05B-member-b-driver-matching.md](05B-member-b-driver-matching.md) | Driver, Matching, reservation and concurrency implementation notes |
| [05C-member-c-payment-messaging-platform.md](05C-member-c-payment-messaging-platform.md) | Payment, messaging platform and reliability implementation notes |

The weekly schedules and hour estimates in these playbooks are historical
planning records. Their invariants, identities and failure-window rules remain
part of the design contract.

## 3. Runtime and verification guides

| Document | Content |
|---|---|
| [authentication-runtime.md](authentication-runtime.md) | JWT issuance, service grants and local key setup |
| [messaging-runtime.md](messaging-runtime.md) | Publisher/consumer lifecycle, confirms, acknowledgement and retry ownership |
| [verification.md](verification.md) | Final verified results, suite counts and evidence index |
| [demo.md](demo.md) | 7–10 minute team demonstration and failure drill |
| [driver-matching-demo.md](driver-matching-demo.md) | Extended Driver/Matching rehearsal runbook |
| [driver-matching-defense.md](driver-matching-defense.md) | Driver/Matching defense workbook |
| [team-technical-report.md](team-technical-report.md) | Technical report source (report.pdf is generated from the consolidated report) |
| [member-a-trip-saga-report.md](member-a-trip-saga-report.md) | Member A technical report |
| [driver-matching-report.md](driver-matching-report.md) | Member B technical report |
| [member-c-reliability-report.md](member-c-reliability-report.md) | Member C technical report |

## 4. Release and provenance records

| Document | Content |
|---|---|
| [release.md](release.md) | Release identity, deployment procedure, verification summary and limitations |
| [report.pdf](report.pdf) | Final project report (academic submission) |
| [slides.pdf](slides.pdf) | Presentation slides |
| [06-history-attribution-correction.md](06-history-attribution-correction.md) | W09/W10 authorship correction and credit-allocation record |
| [07-contribution-mapping.md](07-contribution-mapping.md) | W15/W16 contribution and attribution mapping |
| [08-credit-balance-correction-2026-11-06.md](08-credit-balance-correction-2026-11-06.md) | 2026-11-06 credit-balance correction and provenance map |

Evidence bundles live under [`evidence/`](../evidence/README.md); each bundle
has a manifest that anchors its claims to the source tree it verified.
