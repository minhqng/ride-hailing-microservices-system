---
title: Member B W9 safety and recovery closure
date: 2026-10-25
summary: W9 duplicate safety and persisted same-reservation recovery verified locally; external review gates remain
---

# Member B W9 safety and recovery closure

W9 Phase 3 closed duplicate safety and a real-PostgreSQL REL-02 recovery base, preserving both original B commits and C ancestry. Independent review found missing legacy NO_DRIVER repair and incomplete intent semantic validation; both were fixed. Projection semantic receipts now reject conflicting same-ID deliveries. Implementation commit: d825920d51aaae39ecff31a72540bf45cd800b4a.

Verification selected 880 unique tests, all passing on final applicable source: Matching unit104/integration137, Driver unit117/integration65, Trip unit267/integration130, shared platform60. Targeted tests are subsets. Release build passes. Retained failures include a new test's incorrect cleanup-count assumption, corrected to target ReleaseReservation while preserving total count; a CRLF runner exit error; and a Rabbit startup cookie EACCES likely caused by the controller's root version probe. A fresh full Trip run without that probe passed. The attribution remains inference; no A/C application defect was proven.

The REL-02 seam injects an exception after real Driver commit, then recreates repositories/orchestrator and recovers persisted IN_FLIGHT with the same reservation. It is not an OS crash. External gates: C PR35 remains open, normal human/CI review, historical Driver intent rollout/backfill decision, and future W11 process-kill orchestration. No W10/W11 implementation or remote mutation. See evidence/member-b/w09-outbox/ for claims, commands, hashes and handoff.

> Historical work record — not durable authority. Prefer docs/specs/ADRs for current decisions.
