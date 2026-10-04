CREATE TABLE IF NOT EXISTS payment_operations (
    id uuid PRIMARY KEY,
    operation_key text NOT NULL,
    trip_id uuid NOT NULL,
    operation_type text NOT NULL CHECK (operation_type IN ('AUTHORIZE', 'CAPTURE', 'VOID')),
    amount_minor bigint NOT NULL CHECK (amount_minor > 0),
    currency char(3) NOT NULL,
    state text NOT NULL CHECK (state IN ('PENDING', 'PROCESSING', 'SUCCEEDED', 'DECLINED', 'UNKNOWN', 'REQUIRES_REVIEW')),
    provider_effect_occurred boolean NOT NULL DEFAULT false,
    provider_effect_id text NULL,
    result_code text NULL,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    CONSTRAINT ck_payment_effect_has_id CHECK (NOT provider_effect_occurred OR provider_effect_id IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_payment_operations_trip_id ON payment_operations (trip_id);
