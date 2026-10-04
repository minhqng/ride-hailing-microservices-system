-- Driver DSI-002 state/reservation coherence (defense-in-depth).
--
-- The Driver domain already enforces coherence in memory (AssertCoherence on
-- every transition and on Rehydrate). This constraint makes the same rule
-- impossible-by-construction at the persistence boundary: no direct-SQL,
-- bypassed-domain or torn write can commit an incoherent row that would
-- otherwise detonate at read time as an unhandled 500.
--
-- Mirrors Driver.AssertCoherence exactly:
--   AVAILABLE/OFFLINE carry no reservation or active refs;
--   RESERVED carries trip+reservation and no active trip;
--   ON_TRIP carries the active trip and no reservation refs.
-- Idempotent: safe to re-apply (guarded DDL).

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'chk_drivers_coherence'
  ) THEN
    ALTER TABLE drivers
      ADD CONSTRAINT chk_drivers_coherence
      CHECK (
        (status IN ('AVAILABLE', 'OFFLINE')
          AND reserved_trip_id IS NULL AND reservation_id IS NULL AND active_trip_id IS NULL)
        OR (status = 'RESERVED'
          AND reserved_trip_id IS NOT NULL AND reservation_id IS NOT NULL AND active_trip_id IS NULL)
        OR (status = 'ON_TRIP'
          AND active_trip_id IS NOT NULL AND reserved_trip_id IS NULL AND reservation_id IS NULL)
      );
  END IF;
END $$;
