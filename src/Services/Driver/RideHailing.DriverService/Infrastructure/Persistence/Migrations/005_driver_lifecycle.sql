-- Preserve original reservation identity after the live RESERVED fields clear.
-- Legacy ON_TRIP rows are intentionally not backfilled with guessed identities.
CREATE TABLE IF NOT EXISTS driver_lifecycle_effects (
    driver_id UUID NOT NULL REFERENCES drivers(driver_id),
    trip_id UUID NOT NULL CHECK (trip_id <> '00000000-0000-0000-0000-000000000000'),
    reservation_id UUID NOT NULL CHECK (reservation_id <> '00000000-0000-0000-0000-000000000000'),
    phase TEXT NOT NULL CHECK (phase IN ('STARTED', 'COMPLETED')),
    version INTEGER NOT NULL CHECK (version > 0),
    message_id TEXT NOT NULL UNIQUE,
    occurred_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (driver_id, trip_id, reservation_id, phase),
    UNIQUE (driver_id, trip_id, phase),
    UNIQUE (driver_id, reservation_id, phase)
);

CREATE TABLE IF NOT EXISTS driver_command_receipts (
    message_id TEXT PRIMARY KEY CHECK (length(message_id) BETWEEN 1 AND 256),
    fingerprint TEXT NOT NULL,
    disposition TEXT NOT NULL CHECK (disposition IN ('Applied', 'Replayed', 'Rejected', 'NotFound')),
    version INTEGER NULL CHECK (version > 0),
    event_message_id TEXT NULL,
    reason TEXT NULL,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
