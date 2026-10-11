-- Older 002 deployments used btrim(), which accepts tab/newline-only identities.
-- A new constraint name upgrades those databases and validates existing rows.
-- Fail atomically on corrupt history; never rewrite or delete operation records.
DO $$ BEGIN
IF NOT EXISTS (SELECT 1 FROM pg_constraint
    WHERE conname = 'ck_payment_nonblank_identity' AND conrelid = 'payment_operations'::regclass) THEN
    ALTER TABLE payment_operations ADD CONSTRAINT ck_payment_nonblank_identity CHECK (
        operation_key ~ '\S'
        AND (NOT provider_effect_occurred OR provider_effect_id ~ '\S')
    );
END IF;
END $$;
