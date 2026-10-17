-- W08 hardening (matchingRound lineage): the drivers table now records the
-- matching generation that produced the current reservation. A successful
-- reserve publishes DriverReservedV1 with that generation so Trip's
-- current-generation gate (Doc 02 §21.3, DSI-016) can apply it; the value is
-- durable here so restart, same-reservation replay and delayed publication
-- recover the SAME round, never an invented one.
--
-- Nullability is load-bearing: reservations written before this column carry
-- NULL provenance. Driver never fabricates a generation, so those rows
-- publish DriverReservedV1 WITHOUT matchingRound and Trip keeps parking them
-- as NeedsReview:RESERVATION_ROUND_REQUIRED. RESERVED therefore allows NULL;
-- every transition that clears the reservation refs clears the round too
-- (same coherence constraint below), so a recorded round can never outlive
-- its reservation identity.
-- No data backfill: an unknown historical generation stays unknown.
-- Idempotent: safe to re-apply (guarded DDL). Re-application against a dataset
-- mutated by a broken intermediate deploy fails loud instead of raising an
-- obscure 23514 from ADD CONSTRAINT.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'drivers'
      AND column_name = 'reserved_round'
  ) THEN
    ALTER TABLE drivers
      ADD COLUMN reserved_round INTEGER NULL;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'chk_drivers_reserved_round'
  ) THEN
    ALTER TABLE drivers
      ADD CONSTRAINT chk_drivers_reserved_round
      CHECK (reserved_round IS NULL OR reserved_round >= 1);
  END IF;
END $$;

DO $$
DECLARE
  incoherent integer;
  sample text;
BEGIN
  SELECT COUNT(*) INTO incoherent FROM drivers WHERE NOT (
    (status IN ('AVAILABLE', 'OFFLINE')
      AND reserved_trip_id IS NULL AND reservation_id IS NULL AND active_trip_id IS NULL
      AND reserved_round IS NULL)
    OR (status = 'RESERVED'
      AND reserved_trip_id IS NOT NULL AND reservation_id IS NOT NULL AND active_trip_id IS NULL
      AND (reserved_round IS NULL OR reserved_round >= 1))
    OR (status = 'ON_TRIP'
      AND active_trip_id IS NOT NULL AND reserved_trip_id IS NULL AND reservation_id IS NULL
      AND reserved_round IS NULL)
  );
  IF incoherent > 0 THEN
    SELECT COALESCE(string_agg(driver_id::text, ', '), '') INTO sample
    FROM (SELECT driver_id FROM drivers WHERE NOT (
      (status IN ('AVAILABLE', 'OFFLINE')
        AND reserved_trip_id IS NULL AND reservation_id IS NULL AND active_trip_id IS NULL
        AND reserved_round IS NULL)
      OR (status = 'RESERVED'
        AND reserved_trip_id IS NOT NULL AND reservation_id IS NOT NULL AND active_trip_id IS NULL
        AND (reserved_round IS NULL OR reserved_round >= 1))
      OR (status = 'ON_TRIP'
        AND active_trip_id IS NOT NULL AND reserved_trip_id IS NULL AND reservation_id IS NULL
        AND reserved_round IS NULL)
    ) LIMIT 5) bad;
    RAISE EXCEPTION 'driver reserved_round pre-check failed: % rows violate the round coherence rules (e.g. %); resolve them before applying the extended chk_drivers_coherence', incoherent, sample;
  END IF;

  IF EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'chk_drivers_coherence'
  ) THEN
    ALTER TABLE drivers DROP CONSTRAINT chk_drivers_coherence;
  END IF;

  ALTER TABLE drivers
    ADD CONSTRAINT chk_drivers_coherence
    CHECK (
      (status IN ('AVAILABLE', 'OFFLINE')
        AND reserved_trip_id IS NULL AND reservation_id IS NULL AND active_trip_id IS NULL
        AND reserved_round IS NULL)
      OR (status = 'RESERVED'
        AND reserved_trip_id IS NOT NULL AND reservation_id IS NOT NULL AND active_trip_id IS NULL
        AND (reserved_round IS NULL OR reserved_round >= 1))
      OR (status = 'ON_TRIP'
        AND active_trip_id IS NOT NULL AND reserved_trip_id IS NULL AND reservation_id IS NULL
        AND reserved_round IS NULL)
    );
END $$;
