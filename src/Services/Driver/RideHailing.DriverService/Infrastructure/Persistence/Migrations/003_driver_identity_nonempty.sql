-- Hardening: identity columns must hold real identities, never Guid.Empty.
-- The domain already rejects empty identities (INVALID_IDENTITY); this makes
-- the same rule impossible-by-construction at the persistence boundary so a
-- direct-SQL bypass cannot plant rows that detonate at read time.
-- Idempotent: safe to re-apply (guarded DDL).
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'chk_drivers_identity_nonempty'
  ) THEN
    ALTER TABLE drivers
      ADD CONSTRAINT chk_drivers_identity_nonempty
      CHECK (
        driver_id <> '00000000-0000-0000-0000-000000000000'
        AND user_id <> '00000000-0000-0000-0000-000000000000'
        AND (reserved_trip_id IS NULL OR reserved_trip_id <> '00000000-0000-0000-0000-000000000000')
        AND (reservation_id IS NULL OR reservation_id <> '00000000-0000-0000-0000-000000000000')
        AND (active_trip_id IS NULL OR active_trip_id <> '00000000-0000-0000-0000-000000000000')
        AND (vehicle_id IS NULL OR vehicle_id <> '00000000-0000-0000-0000-000000000000')
      );
  END IF;
END $$;
