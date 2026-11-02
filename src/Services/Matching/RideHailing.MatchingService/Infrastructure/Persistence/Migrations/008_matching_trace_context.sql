-- Diagnostic parentage survives the durable worker handoff and process restart.
-- Existing rounds remain valid without trace context; business state is unchanged.
ALTER TABLE matching_rounds
    ADD COLUMN IF NOT EXISTS intake_traceparent text NULL
        CHECK (intake_traceparent IS NULL OR length(intake_traceparent) <= 128);
