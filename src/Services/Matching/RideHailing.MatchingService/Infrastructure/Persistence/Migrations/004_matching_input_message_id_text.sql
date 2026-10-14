-- B-W08 message-identity fix: store the canonical envelope MessageId verbatim.
-- Doc 02 §5.2–5.3 (envelope identity) + §12.2 (MatchingRound persistence):
-- the broker input identity is an opaque string (C's MessageEnvelope.MessageId),
-- not a UUID. The W5 UUID column rejected legitimate non-UUID envelope IDs
-- (e.g. ULID "01K8EXAMPLEMESSAGE").
--
-- Forward-only, append-only: never edits 001/002/003. Preserves every row via
-- UUID::text (canonical lowercase dashed form), preserves PK
-- (trip_id, matching_round), preserves UNIQUE(input_message_id), preserves
-- NOT NULL and all other indexes/constraints. Idempotent: safe to re-apply
-- (fresh databases apply 001→004 in order; upgrades apply 004 once).
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'matching_rounds'
      AND column_name = 'input_message_id'
      AND data_type = 'uuid'
  ) THEN
    ALTER TABLE matching_rounds
      ALTER COLUMN input_message_id TYPE TEXT
      USING input_message_id::text;
  END IF;
END $$;
