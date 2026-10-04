-- B-W02-03 Driver PostgreSQL schema baseline (W2, no business logic).
-- Authority: Driver/Vehicle/Location (Doc 00 §3.2, 05B §4.1-4.2).
-- W3+ adds FSM/OCC/fencing on top. W6 adds stronger DB defense if needed.
-- Idempotent: safe to re-apply (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS drivers (
    driver_id       UUID PRIMARY KEY,
    user_id         UUID NOT NULL,
    status          TEXT NOT NULL CHECK (status IN ('OFFLINE','AVAILABLE','RESERVED','ON_TRIP')),
    version         INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    reserved_trip_id UUID NULL,
    reservation_id   UUID NULL,
    active_trip_id   UUID NULL,
    vehicle_id       UUID NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Stable reservation identity must be unique when present (DSI-005 groundwork).
CREATE UNIQUE INDEX IF NOT EXISTS ux_drivers_reservation_id
    ON drivers (reservation_id) WHERE reservation_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS vehicles (
    vehicle_id   UUID PRIMARY KEY,
    driver_id    UUID NOT NULL REFERENCES drivers (driver_id) ON DELETE RESTRICT,
    plate        TEXT NOT NULL,
    vehicle_type TEXT NOT NULL,
    active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_vehicles_plate ON vehicles (plate);
CREATE INDEX IF NOT EXISTS ix_vehicles_driver_id ON vehicles (driver_id);
