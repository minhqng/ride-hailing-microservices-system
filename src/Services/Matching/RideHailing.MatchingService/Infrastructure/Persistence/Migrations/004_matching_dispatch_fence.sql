-- W07 reconciliation fencing: durable max-dispatched Driver version per attempt.
-- Safety: Matching must not treat current AVAILABLE as proof that an earlier
-- outstanding Reserve(R) cannot commit later (DSI-017/031). The Driver query
-- returns current version Vx; every Reserve(R) was dispatched with some
-- expectedVersion V. Versions only increase (Driver.Touch). If Vx is still
-- within the dispatched range (Vx <= max dispatched), R may still commit, so
-- reconciliation must stay UNKNOWN. Only Vx > max proves all outstanding
-- fences stale (future reads see >= Vx > any dispatched V, so StaleVersion).
-- Other negatives (ReservedByOther/Offline/OnTrip/NotFound) are conclusive
-- without version: any future R read sees incompatible state or missing row
-- (no DELETE in production; row lock serializes writers), so cannot commit.
-- NULL = legacy row without trustworthy provenance: treat as UNKNOWN, never
-- safe to advance. Idempotent: safe to re-apply. Fresh bootstrap includes it
-- via filename-ordered loader; upgrades from W06/W07 add it without data loss.
ALTER TABLE matching_attempts
    ADD COLUMN IF NOT EXISTS max_dispatched_version INTEGER NULL CHECK (max_dispatched_version IS NULL OR max_dispatched_version >= 1);
