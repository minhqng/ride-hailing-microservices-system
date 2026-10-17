-- W08 hardening: at most one ledger attempt per (trip_id, matching_round, driver_id).
--
-- The compute path already treats "one attempt per driver per round" as the
-- invariant: CandidateRanker output is deduplicated by driver (byDriver) and
-- insert conflicts converge by reloading the winner's row for the same driver
-- (InsertConvergentAsync). But no DB constraint enforced it: two computes for
-- the same round that race past each other's inserts can still mint TWO
-- ordinals for one driver with two distinct reservationIds — neither the
-- attempts PK (trip, round, ordinal) nor the reservation_id unique index
-- prevents that. Driver authority still serializes the two reserve calls
-- (the second conflicts), so this never double-reserves, but the ledger
-- would carry a spurious attempt identity for the round.
--
-- Adding the constraint cannot reject legal behavior: within a round a
-- REJECTED driver is excluded from replay and eligibility, resume replays the
-- durable row's own ordinal, and cross-round attempts live on other PK rows.
-- Dirty datasets fail loud with the duplicate groups listed (same style as
-- chk_drivers_coherence: no silent cleanup, no fabricated merge).
-- Idempotent: safe to re-apply (guarded DDL).
DO $$
DECLARE
  violating integer;
  sample text;
BEGIN
  SELECT COUNT(*) INTO violating FROM (
    SELECT trip_id, matching_round, driver_id
    FROM matching_attempts
    GROUP BY trip_id, matching_round, driver_id
    HAVING COUNT(*) > 1
  ) d;
  IF violating > 0 THEN
    SELECT COALESCE(string_agg(trip_id::text || '/' || matching_round || '/' || driver_id::text, ', '), '')
      INTO sample
    FROM (
      SELECT trip_id, matching_round, driver_id
      FROM matching_attempts
      GROUP BY trip_id, matching_round, driver_id
      HAVING COUNT(*) > 1
      LIMIT 5
    ) bad;
    RAISE EXCEPTION 'matching attempt pre-check failed: % duplicate (trip, round, driver) groups (e.g. %); deduplicate them before applying ux_matching_attempts_trip_round_driver', violating, sample;
  END IF;

  CREATE UNIQUE INDEX IF NOT EXISTS ux_matching_attempts_trip_round_driver
    ON matching_attempts (trip_id, matching_round, driver_id);
END $$;
