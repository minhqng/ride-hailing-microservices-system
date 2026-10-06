DO $$ BEGIN
IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_payment_coherence' AND conrelid = 'payment_operations'::regclass) THEN
    ALTER TABLE payment_operations ADD CONSTRAINT ck_payment_coherence CHECK (
        btrim(operation_key) <> '' AND trip_id <> '00000000-0000-0000-0000-000000000000'
        AND currency ~ '^[A-Z]{3}$'
        AND updated_at >= created_at
        AND (state = 'PENDING' OR attempt_count > 0)
        AND (provider_effect_occurred = (provider_effect_id IS NOT NULL AND btrim(provider_effect_id) <> ''))
        AND (state <> 'SUCCEEDED' OR provider_effect_occurred)
        AND (state NOT IN ('PENDING','PROCESSING','DECLINED') OR NOT provider_effect_occurred)
        AND ((state IN ('SUCCEEDED','DECLINED')) = (completed_at IS NOT NULL))
        AND (completed_at IS NULL OR (completed_at >= created_at AND completed_at <= updated_at))
    );
END IF;
END $$;
