-- Durable terminal-result identity for NO_DRIVER. Forward-only and append-only:
-- 001-005 are untouched.
--
-- result_message_id already exists (UUID NULL) from 001. It is the
-- canonical persisted identity, equal to Outbox message_id and the wire
-- envelope messageId. A terminal NO_DRIVER without that identity would make
-- an exact replay unable to reuse one logical result, so new terminal writes
-- must supply it. NOT VALID deliberately preserves any legacy NO_DRIVER rows
-- created earlier; PostgreSQL still enforces the check for every new or
-- updated row.
--
-- The partial unique index makes one logical terminal result map to exactly
-- one identity across rounds (non-NULL values only), while legacy NULL rows
-- remain legal until explicitly repaired. Matching result identities are
-- domain-separated per (trip_id, matching_round), so a collision is a defect
-- and must fail loud.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'chk_matching_rounds_no_driver_result'
      AND conrelid = 'matching_rounds'::regclass
  ) THEN
    ALTER TABLE matching_rounds
      ADD CONSTRAINT chk_matching_rounds_no_driver_result
      CHECK (status <> 'NO_DRIVER' OR result_message_id IS NOT NULL) NOT VALID;
  END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_matching_rounds_result_message_id
    ON matching_rounds (result_message_id)
    WHERE result_message_id IS NOT NULL;
