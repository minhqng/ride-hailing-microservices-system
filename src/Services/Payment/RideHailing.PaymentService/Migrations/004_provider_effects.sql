-- Simulator authority, committed separately from Payment's caller-visible outcome.
CREATE TABLE IF NOT EXISTS provider_effects (
    operation_key text COLLATE "C" PRIMARY KEY,
    request_fingerprint text NOT NULL,
    effect_id text NOT NULL UNIQUE,
    committed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
