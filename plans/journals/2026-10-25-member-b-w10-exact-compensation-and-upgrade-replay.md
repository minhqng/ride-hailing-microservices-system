---
title: Member B W10 exact compensation and upgrade replay
date: 2026-10-25
summary: "Four-field fencing, durable dispositions and real infrastructure proofs; C integration remains external"
---

# Member B W10 exact compensation and upgrade replay

## What happened

The live A Release builder carries driverId, tripId, reservationId and matchingRound. B retained the round in its semantic fingerprint but did not validate it against Driver reservation provenance. Broker Release also returned an unproven replay on AVAILABLE. Both gaps are now guarded without changing the W7 HTTP path.

## Review repair

Independent review caught new round validation running before receipt lookup, breaking replay of committed older three-field commands. The upgrade regression failed before repair. Exact semantic receipt lookup now precedes new execution validation, preserving historical replay without guessing a round or affecting newer reservations.

## Verification and boundary

Real PG/RabbitMQ proofs cover rollback, redelivery, exact Trip intake, duplicate relay publication and infrastructure restart. A W10 PR40 exists, but C Payment broker command/outcome transport is absent. Full REL-09 remains an external gate, not a synthetic payment test claim. Final results and publication are recorded in the W10 evidence manifest and PR39.

> Historical work record — not durable authority. Prefer docs/specs/ADRs for current decisions.
