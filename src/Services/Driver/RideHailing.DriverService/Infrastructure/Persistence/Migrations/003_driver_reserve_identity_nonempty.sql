-- B-W06-04 DB defense: reserve/active identity must never be Guid.Empty.
--
-- The Driver domain rejects empty trip/reservation/active refs on every
-- transition, and the OCC reserve path validates them at the API boundary.
-- This constraint makes the same rule impossible-by-construction at the
-- persistence boundary: no direct-SQL, bypassed-domain or torn write can
-- commit a RESERVED/ON_TRIP row carrying a corrupt empty identity that would
-- otherwise collide across drivers or detonate at read time.
-- Faithful to frozen invariant "empty != absent" (Doc 01 §2.1 guard
-- philosophy, DSI-005 stable-identity groundwork).
-- Idempotent: safe to re-apply (guarded DDL). Dirty datasets fail loud with
-- PG 23514 (no silent cleanup, no backfill: empties are never legitimate).
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'chk_drivers_reserve_identity_nonempty'
  ) THEN
    ALTER TABLE drivers
      ADD CONSTRAINT chk_drivers_reserve_identity_nonempty
      CHECK (
        (reserved_trip_id IS NULL OR reserved_trip_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        AND (reservation_id IS NULL OR reservation_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        AND (active_trip_id IS NULL OR active_trip_id <> '00000000-0000-0000-0000-000000000000'::uuid)
      );
  END IF;
END $$;
