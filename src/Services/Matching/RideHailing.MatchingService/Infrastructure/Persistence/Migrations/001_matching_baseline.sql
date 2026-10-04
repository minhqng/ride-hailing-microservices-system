-- B-W02-04 Matching PostgreSQL schema baseline (W2, no ranking/reserve logic yet).
-- Authority: MatchingRound/MatchingAttempt (Doc 00 §4.3-4.4, 05B §4.3-4.4).
-- Matching must NEVER access driver_db (05B §11.3). This file applies to matching_db only.

CREATE TABLE IF NOT EXISTS matching_rounds (
    trip_id                 UUID NOT NULL,
    matching_round          INTEGER NOT NULL CHECK (matching_round >= 1),
    input_message_id        UUID NOT NULL,
    status                  TEXT NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN','SEARCHING','RESERVED','NO_DRIVER','CANCELLED')),
    current_stage           TEXT NOT NULL DEFAULT 'S0',
    cancel_requested        BOOLEAN NOT NULL DEFAULT FALSE,
    ranking_config_version  TEXT NOT NULL DEFAULT 'v1',
    candidate_snapshot_hash TEXT NULL,
    selected_driver_id      UUID NULL,
    selected_reservation_id UUID NULL,
    result_message_id       UUID NULL,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (trip_id, matching_round)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_matching_rounds_input_message_id
    ON matching_rounds (input_message_id);

CREATE TABLE IF NOT EXISTS matching_attempts (
    trip_id        UUID NOT NULL,
    matching_round INTEGER NOT NULL,
    ordinal        INTEGER NOT NULL CHECK (ordinal >= 0),
    driver_id      UUID NOT NULL,
    reservation_id UUID NOT NULL,
    state          TEXT NOT NULL DEFAULT 'READY'
        CHECK (state IN ('READY','IN_FLIGHT','UNKNOWN','REJECTED','RESERVED')),
    last_error_code TEXT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (trip_id, matching_round, ordinal),
    FOREIGN KEY (trip_id, matching_round)
        REFERENCES matching_rounds (trip_id, matching_round) ON DELETE CASCADE
);

-- One logical reserve attempt = one reservationId (DSI-018 groundwork).
CREATE UNIQUE INDEX IF NOT EXISTS ux_matching_attempts_reservation_id
    ON matching_attempts (reservation_id);
CREATE INDEX IF NOT EXISTS ix_matching_attempts_driver_id
    ON matching_attempts (driver_id);
