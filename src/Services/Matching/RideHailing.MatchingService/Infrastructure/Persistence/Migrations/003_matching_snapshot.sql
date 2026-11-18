-- Candidate snapshot persistence for the deterministic pipeline.
-- The snapshot carries candidate_count and next_attempt_ordinal; its JSON
-- stores the ordered candidate list so replays/resumes reproduce the exact
-- order without recomputing from possibly-changed Redis.
-- Idempotent: safe to re-apply.
ALTER TABLE matching_rounds
    ADD COLUMN IF NOT EXISTS candidate_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS next_attempt_ordinal INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS candidate_snapshot_json JSONB NULL;
