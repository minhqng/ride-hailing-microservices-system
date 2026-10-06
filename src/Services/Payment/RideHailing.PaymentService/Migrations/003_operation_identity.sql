-- Fail on historical duplicates; never silently discard financial records.
CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_operation_key ON payment_operations ((operation_key COLLATE "C"));
ALTER TABLE payment_operations ADD COLUMN IF NOT EXISTS request_fingerprint text
    GENERATED ALWAYS AS (operation_type || ':' || trip_id::text || ':' || amount_minor::text || ':' || currency::text) STORED;
