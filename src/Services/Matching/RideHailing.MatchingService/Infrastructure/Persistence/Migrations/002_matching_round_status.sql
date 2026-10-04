-- B-W02-04 fix (P0 review): canonical MatchingRound status vocabulary per Doc 01 §8.2.
-- 001 froze a wrong set (OPEN/SEARCHING invented; PROCESSING/FAILED missing).
-- Migrations are append-only: this file corrects 001 instead of editing it.
-- Idempotent: safe to re-apply.

-- 1. Drop the legacy CHECK on matching_rounds.status (auto-named by PostgreSQL in 001).
--    The drop MUST precede the backfill: the UPDATE below writes 'PROCESSING',
--    which violates the still-enforced legacy vocabulary and would abort the
--    migration mid-script on any dataset containing legacy rows.
DO $$
DECLARE c RECORD;
BEGIN
  FOR c IN
    SELECT con.conname
    FROM pg_constraint con
    JOIN pg_class rel ON rel.oid = con.conrelid
    JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = ANY (con.conkey)
    WHERE rel.relname = 'matching_rounds'
      AND att.attname = 'status'
      AND con.contype = 'c'
      AND con.conname <> 'chk_matching_rounds_status'
  LOOP
    EXECUTE format('ALTER TABLE matching_rounds DROP CONSTRAINT %I', c.conname);
  END LOOP;
END $$;

-- 2. Migrate any rows written under the old vocabulary to the canonical equivalent.
--    Both OPEN and SEARCHING were pre-terminal working states -> PROCESSING.
--    Runs AFTER the legacy CHECK drop (step 1) so the backfilled value is
--    legal, and BEFORE the canonical CHECK add (step 3) so legacy rows can
--    no longer exist when the new vocabulary is enforced.
UPDATE matching_rounds SET status = 'PROCESSING' WHERE status IN ('OPEN', 'SEARCHING');

-- 3. Canonical CHECK + default (Doc 01 §8.2: PROCESSING/RESERVED/NO_DRIVER/CANCELLED/FAILED).
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'chk_matching_rounds_status'
  ) THEN
    ALTER TABLE matching_rounds
      ADD CONSTRAINT chk_matching_rounds_status
      CHECK (status IN ('PROCESSING','RESERVED','NO_DRIVER','CANCELLED','FAILED'));
  END IF;
END $$;

ALTER TABLE matching_rounds ALTER COLUMN status SET DEFAULT 'PROCESSING';
