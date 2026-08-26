CREATE SCHEMA IF NOT EXISTS autocheck;
CREATE SCHEMA IF NOT EXISTS api;

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_runtime') THEN
        CREATE ROLE course_runtime NOLOGIN;
    END IF;
END $$;

GRANT USAGE ON SCHEMA autocheck TO course_runtime;
GRANT USAGE ON SCHEMA api TO course_runtime;

CREATE TABLE IF NOT EXISTS autocheck.contract_info (
    contract_version text PRIMARY KEY,
    registered_at    timestamptz NOT NULL DEFAULT now()
);
INSERT INTO autocheck.contract_info (contract_version)
VALUES ('course-1') ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS autocheck.schema_migrations (
    file_name  text PRIMARY KEY,
    checksum   text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS autocheck.action_definitions (
    id            bigserial PRIMARY KEY,
    module        text NOT NULL,
    action        text NOT NULL,
    version       integer NOT NULL,
    manifest      jsonb NOT NULL,
    manifest_hash text NOT NULL,
    enabled       boolean NOT NULL,
    is_default    boolean NOT NULL,
    published_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (module, action, version)
);

CREATE TABLE IF NOT EXISTS autocheck.action_dispatches (
    id             bigserial PRIMARY KEY,
    module         text NOT NULL,
    action         text NOT NULL,
    version        integer NOT NULL,
    request_id     text,
    correlation_id uuid NOT NULL,
    payload_hash   text NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS ix_dispatches_route
    ON autocheck.action_dispatches (module, action);
CREATE INDEX IF NOT EXISTS ix_dispatches_request
    ON autocheck.action_dispatches (request_id);

CREATE TABLE IF NOT EXISTS autocheck.operations (
    operation_id    uuid PRIMARY KEY,
    request_id      text NOT NULL,
    idempotency_key text NOT NULL,
    scope_key       text NOT NULL,
    module          text NOT NULL,
    action          text NOT NULL,
    version         integer NOT NULL,
    operation_kind  text,
    status          text NOT NULL,
    process_id      text,
    amount          numeric(18,2),
    currency        text,
    payload         jsonb NOT NULL,
    payload_hash    text NOT NULL,
    outcome         text,
    result          jsonb,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    finished_at     timestamptz,
    UNIQUE (scope_key, idempotency_key)
);

CREATE TABLE IF NOT EXISTS autocheck.operation_events (
    event_id     uuid PRIMARY KEY,
    operation_id uuid NOT NULL,
    event_type   text NOT NULL,
    payload_hash text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT clock_timestamp()
);

REVOKE ALL ON autocheck.operations FROM course_runtime;
REVOKE ALL ON autocheck.operation_events FROM course_runtime;
GRANT SELECT ON autocheck.contract_info, autocheck.action_definitions TO course_runtime;
GRANT SELECT ON autocheck.action_dispatches TO course_runtime;