-- Durable consumer dedup schema. The marker row is inserted inside the
-- owning service's local transaction, immediately before the business effect.
-- A rollback removes both; a duplicate delivery finds the committed marker and
-- applies no second effect.
CREATE TABLE IF NOT EXISTS processed_messages (
    consumer_scope  text        NOT NULL,
    message_id      text        NOT NULL,
    message_type    text        NOT NULL,
    processed_at    timestamptz NOT NULL,
    handler_version text        NULL,
    aggregate_id    text        NULL,
    CONSTRAINT processed_messages_pk PRIMARY KEY (consumer_scope, message_id),
    CONSTRAINT processed_messages_identity_nonempty CHECK (
        btrim(consumer_scope) <> ''
        AND btrim(message_id) <> ''
        AND btrim(message_type) <> ''
    )
);

-- Retention/audit support. The primary key is the hot duplicate-check path.
CREATE INDEX IF NOT EXISTS ix_processed_messages_processed_at
    ON processed_messages (processed_at);
