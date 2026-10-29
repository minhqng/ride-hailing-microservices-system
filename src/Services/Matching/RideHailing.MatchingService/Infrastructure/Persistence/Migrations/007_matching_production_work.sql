-- B-REC-01/DSI-021: durable production work scheduling and progressive stages.
-- Forward-only and append-only: 001-006 are untouched.
--
-- matching_rounds gains the durable intake needed to execute without the
-- broker message (pickup center) and the durable scheduling bit that makes a
-- round discoverable by the autonomous worker before ACK. `auto_execute`
-- separates production rounds from the W5 internal/demo and W11 test seams:
-- only a canonical RideRequestedV1 intake sets it.
--
-- matching_attempts gains the logical stage its ordinal belongs to, so a
-- stage's attempts can be conclusively exhausted before the round advances
-- (DSI-021/022), plus last_checked_at for bounded recovery backoff.
--
-- matching_cancel_intents records a CancelMatchingRoundV1 that arrives before
-- its RideRequestedV1 (independent queues have no cross-queue ordering). The
-- ride-requested intake consumes the intent atomically, so a cancel is never
-- lost because the round did not exist yet.
-- Idempotent: safe to re-apply.

ALTER TABLE matching_rounds
    ADD COLUMN IF NOT EXISTS pickup_longitude DOUBLE PRECISION NULL
        CHECK (pickup_longitude IS NULL OR (pickup_longitude >= -180 AND pickup_longitude <= 180)),
    ADD COLUMN IF NOT EXISTS pickup_latitude DOUBLE PRECISION NULL
        CHECK (pickup_latitude IS NULL OR (pickup_latitude >= -90 AND pickup_latitude <= 90)),
    ADD COLUMN IF NOT EXISTS auto_execute BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS work_checked_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS work_error_code TEXT NULL,
    ADD COLUMN IF NOT EXISTS stage_evaluated TEXT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_matching_rounds_production_intake'
          AND conrelid = 'matching_rounds'::regclass
    ) THEN
        ALTER TABLE matching_rounds
            ADD CONSTRAINT chk_matching_rounds_production_intake
            CHECK (auto_execute = FALSE
                OR (pickup_longitude IS NOT NULL AND pickup_latitude IS NOT NULL))
            NOT VALID;
    END IF;
END $$;

ALTER TABLE matching_attempts
    ADD COLUMN IF NOT EXISTS stage TEXT NOT NULL DEFAULT 'S0',
    ADD COLUMN IF NOT EXISTS last_checked_at TIMESTAMPTZ NULL;

CREATE TABLE IF NOT EXISTS matching_cancel_intents (
    trip_id        UUID NOT NULL,
    matching_round INTEGER NOT NULL CHECK (matching_round >= 1),
    requested_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    consumed_at    TIMESTAMPTZ NULL,
    PRIMARY KEY (trip_id, matching_round)
);

-- Worker discovery: PROCESSING production rounds that are due for a check.
CREATE INDEX IF NOT EXISTS ix_matching_rounds_work
    ON matching_rounds (work_checked_at, updated_at)
    WHERE status = 'PROCESSING' AND auto_execute = TRUE;

-- Doc 01 §8.3: CANCELLED/SKIPPED is a possible extended terminal. A
-- cancellation closes READY attempts (no effect was sent) without pretending
-- they were Driver rejections.
DO $$
DECLARE c RECORD;
BEGIN
    FOR c IN
        SELECT con.conname
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = ANY (con.conkey)
        WHERE rel.relname = 'matching_attempts'
          AND att.attname = 'state'
          AND con.contype = 'c'
          AND con.conname <> 'chk_matching_attempts_state'
    LOOP
        EXECUTE format('ALTER TABLE matching_attempts DROP CONSTRAINT %I', c.conname);
    END LOOP;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_matching_attempts_state'
          AND conrelid = 'matching_attempts'::regclass
    ) THEN
        ALTER TABLE matching_attempts
            ADD CONSTRAINT chk_matching_attempts_state
            CHECK (state IN ('READY','IN_FLIGHT','UNKNOWN','REJECTED','RESERVED','CANCELLED'));
    END IF;
END $$;
