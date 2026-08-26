CREATE SCHEMA IF NOT EXISTS autocheck;
CREATE SCHEMA IF NOT EXISTS api;

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_runtime') THEN CREATE ROLE course_runtime NOLOGIN; END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_api') THEN CREATE ROLE course_api NOLOGIN; END IF;
END $$;

GRANT USAGE ON SCHEMA autocheck TO course_runtime;
GRANT USAGE ON SCHEMA autocheck TO course_api;
GRANT USAGE ON SCHEMA api TO course_runtime;
GRANT USAGE ON SCHEMA api TO course_api;

-- Контрактная таблица contract_info (сразу с _tbl)
CREATE TABLE IF NOT EXISTS autocheck.contract_info_tbl (
    contract_version text PRIMARY KEY,
    generated_at     timestamptz NOT NULL DEFAULT now()
);
INSERT INTO autocheck.contract_info_tbl (contract_version) VALUES ('course-1') ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS autocheck.schema_migrations (
    file_name  text PRIMARY KEY,
    checksum   text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);

-- Физические таблицы сразу с _tbl
CREATE TABLE IF NOT EXISTS autocheck.action_definitions_tbl (
    id              bigserial PRIMARY KEY,
    module          text NOT NULL,
    action          text NOT NULL,
    version         integer NOT NULL,
    http_method     text NOT NULL DEFAULT 'POST',
    target_schema   text NOT NULL,
    target_function text NOT NULL,
    outcomes        jsonb NOT NULL DEFAULT '[]'::jsonb,
    manifest        jsonb NOT NULL,
    manifest_hash   text NOT NULL,
    enabled         boolean NOT NULL,
    is_default      boolean NOT NULL,
    published_at    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (module, action, version)
);

CREATE TABLE IF NOT EXISTS autocheck.action_dispatches_tbl (
    id             bigserial PRIMARY KEY,
    module         text NOT NULL,
    action         text NOT NULL,
    version        integer NOT NULL,
    request_id     text,
    correlation_id uuid NOT NULL,
    principal      text NOT NULL DEFAULT '',
    payload_hash   text NOT NULL,
    status         text NOT NULL DEFAULT 'OK',
    outcome        text,
    occurred_at    timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS ix_dispatches_route ON autocheck.action_dispatches_tbl (module, action);
CREATE INDEX IF NOT EXISTS ix_dispatches_request ON autocheck.action_dispatches_tbl (request_id);

CREATE TABLE IF NOT EXISTS autocheck.operations_tbl (
    operation_id    uuid PRIMARY KEY,
    request_id      text NOT NULL,
    idempotency_key text NOT NULL,
    scope_key       text NOT NULL,
    module          text NOT NULL,
    action          text NOT NULL,
    version         integer NOT NULL,
    operation_kind  text,
    status          text NOT NULL,
    process_id      uuid,
    amount          numeric(18,2),
    currency        text,
    payload         jsonb NOT NULL,
    payload_hash    text NOT NULL,
    outcome         text,
    result          jsonb,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at      timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS ix_operations_scope_key ON autocheck.operations_tbl (scope_key, idempotency_key);

CREATE TABLE IF NOT EXISTS autocheck.operation_events_tbl (
    event_id     uuid PRIMARY KEY,
    operation_id uuid NOT NULL,
    event_type   text NOT NULL,
    payload_hash text NOT NULL,
    occurred_at  timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- КРИТИЧНО: права для course_api (владельца SECURITY DEFINER функций)
GRANT SELECT, INSERT, UPDATE, DELETE ON
    autocheck.contract_info_tbl,
    autocheck.action_definitions_tbl,
    autocheck.action_dispatches_tbl,
    autocheck.operations_tbl,
    autocheck.operation_events_tbl
TO course_api;

-- sequence для bigserial PRIMARY KEY
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA autocheck TO course_api;