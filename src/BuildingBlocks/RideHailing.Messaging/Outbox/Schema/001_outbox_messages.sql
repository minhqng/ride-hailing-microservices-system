-- Shared Transactional Outbox schema. Applied by the owning service
-- against its own PostgreSQL database; never shared across service DBs.
CREATE TABLE IF NOT EXISTS outbox_messages (
    message_id        text        NOT NULL,
    message_type      text        NOT NULL,
    schema_version    integer     NOT NULL,
    correlation_id    text        NOT NULL,
    causation_id      text        NULL,
    aggregate_type    text        NULL,
    aggregate_id      text        NULL,
    aggregate_version bigint      NULL,
    saga_id           text        NULL,
    operation_key     text        NULL,
    payload_json      jsonb       NOT NULL,
    headers_json      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    exchange          text        NOT NULL,
    routing_key       text        NOT NULL,
    occurred_at       timestamptz NOT NULL,
    status            text        NOT NULL DEFAULT 'PENDING',
    attempt_count     integer     NOT NULL DEFAULT 0,
    next_attempt_at   timestamptz NOT NULL,
    claimed_by        text        NULL,
    claimed_at        timestamptz NULL,
    published_at      timestamptz NULL,
    last_error_code   text        NULL,
    last_error_at     timestamptz NULL,
    CONSTRAINT outbox_messages_pk PRIMARY KEY (message_id),
    CONSTRAINT outbox_messages_status_check
        CHECK (status IN ('PENDING', 'PUBLISHED', 'FAILED_VISIBLE')),
    CONSTRAINT outbox_messages_schema_version_check CHECK (schema_version >= 1),
    CONSTRAINT outbox_messages_attempt_count_check CHECK (attempt_count >= 0),
    CONSTRAINT outbox_messages_identity_nonempty CHECK (
        btrim(message_id) <> ''
        AND btrim(message_type) <> ''
        AND btrim(correlation_id) <> ''
        AND btrim(exchange) <> ''
        AND btrim(routing_key) <> ''
    )
);

-- Claim path: pending rows due now, ordered by age. Partial index keeps the
-- relay scan bounded even when most rows are already published.
CREATE INDEX IF NOT EXISTS ix_outbox_messages_claim
    ON outbox_messages (next_attempt_at, occurred_at, message_id)
    WHERE status = 'PENDING';

-- Operator/auditor query for the visible terminal failure set.
CREATE INDEX IF NOT EXISTS ix_outbox_messages_failed_visible
    ON outbox_messages (occurred_at, message_id)
    WHERE status = 'FAILED_VISIBLE';
