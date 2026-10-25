---
title: Member B W10 durable Driver lifecycle
date: 2026-10-25
summary: "Lifecycle, receipt and Outbox atomicity verified; Phase 2 remains separate"
---

# Member B W10 durable Driver lifecycle

Implemented durable Driver Start/Complete with an append-only lifecycle ledger and atomic semantic receipts, state and shared Outbox intent. All three shared-queue commands are supported. Original reservation identity survives cleared live fields and later trips.

Initial focused tests exposed two test setup mistakes (legacy migrations assumed public schema; shared relay crash seam needed expected exception/expired lease). Both were corrected without weakening assertions. Independent review then found disabled runtime ingress and repeated migration DDL on broker reconnect. Enabled the documented default and proved reconnect while PostgreSQL blocks DDL.

Final selected local suites: 913 PASS, 0 FAIL/SKIP; Release build PASS with one pre-existing analyzer warning. Independent final P0/P1 review clean. Code commit b1435f79d1fd5b29696de68eeeaedcbd989e7a96. Evidence and exact Phase 2/A-producer/human-review gates live in evidence/member-b/w10-lifecycle. Ready-for-Review stacked publication and exact-head CI follow; no merge. AgentWiki publish skipped.

> Historical work record — not durable authority. Prefer docs/specs/ADRs for current decisions.
