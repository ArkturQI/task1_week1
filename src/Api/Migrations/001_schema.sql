CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS autocheck;
CREATE SCHEMA IF NOT EXISTS api;
CREATE SCHEMA IF NOT EXISTS opencheck;

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_runtime') THEN
        CREATE ROLE course_runtime NOLOGIN;
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_api') THEN
        CREATE ROLE course_api NOLOGIN;
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'api_owner') THEN
        CREATE ROLE api_owner NOLOGIN NOSUPERUSER;
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_migration_login') THEN
        CREATE ROLE course_migration_login
            WITH LOGIN
            PASSWORD 'migration_secret_change_me'
            CREATEROLE;
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_api_login') THEN
        CREATE ROLE course_api_login
            WITH LOGIN
            PASSWORD 'api_secret_change_me';
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_cli_login') THEN
        CREATE ROLE course_cli_login
            WITH LOGIN
            PASSWORD 'cli_secret_change_me';
    END IF;
END $$;

GRANT CONNECT, CREATE
ON DATABASE course
TO course_migration_login;

GRANT CONNECT
ON DATABASE course
TO course_api_login, course_cli_login;

GRANT CREATE
ON DATABASE course
TO course_cli_login;

GRANT api_owner
TO course_migration_login
WITH ADMIN OPTION;

GRANT api_owner
TO course_cli_login
WITH ADMIN OPTION;

GRANT CREATE
ON SCHEMA api
TO api_owner;

GRANT CREATE
ON SCHEMA opencheck
TO course_cli_login;

GRANT course_runtime
TO course_api_login;

GRANT course_api
TO course_cli_login;

GRANT USAGE
ON SCHEMA autocheck
TO course_runtime,
   course_api,
   api_owner,
   course_api_login,
   course_cli_login,
   course_migration_login;

GRANT USAGE
ON SCHEMA api
TO course_runtime,
   course_api,
   api_owner,
   course_api_login,
   course_cli_login,
   course_migration_login;

GRANT USAGE
ON SCHEMA opencheck
TO api_owner,
   course_runtime,
   course_cli_login,
   course_migration_login;

GRANT CREATE
ON SCHEMA autocheck
TO course_cli_login,
   course_migration_login;

CREATE TABLE IF NOT EXISTS autocheck.contract_info (
    contract_version text PRIMARY KEY,
    generated_at     timestamptz NOT NULL DEFAULT now()
);

INSERT INTO autocheck.contract_info (contract_version)
VALUES ('course-1')
ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS autocheck.schema_migrations (
    file_name  text PRIMARY KEY,
    checksum   text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS autocheck.action_definitions (
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

CREATE UNIQUE INDEX IF NOT EXISTS idx_action_default_unique
    ON autocheck.action_definitions (module, action)
    WHERE is_default = true;

CREATE OR REPLACE FUNCTION autocheck.enforce_exactly_one_default()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    v_module text;
    v_action text;
    v_enabled_count bigint;
    v_default_count bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        v_module := OLD.module;
        v_action := OLD.action;
    ELSE
        v_module := NEW.module;
        v_action := NEW.action;
    END IF;

    SELECT count(*)
    INTO v_enabled_count
    FROM autocheck.action_definitions
    WHERE module = v_module
      AND action = v_action
      AND enabled = true;

    IF v_enabled_count = 0 THEN
        RETURN NULL;
    END IF;

    SELECT count(*)
    INTO v_default_count
    FROM autocheck.action_definitions
    WHERE module = v_module
      AND action = v_action
      AND enabled = true
      AND is_default = true;

    IF v_default_count <> 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = format(
                'route %s.%s must have exactly one enabled default version',
                v_module,
                v_action
            );
    END IF;

    RETURN NULL;
END;
$$;

ALTER FUNCTION autocheck.enforce_exactly_one_default()
OWNER TO api_owner;

DROP TRIGGER IF EXISTS trg_action_definitions_exactly_one_default
ON autocheck.action_definitions;

CREATE CONSTRAINT TRIGGER trg_action_definitions_exactly_one_default
AFTER INSERT OR UPDATE OR DELETE
ON autocheck.action_definitions
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW
EXECUTE FUNCTION autocheck.enforce_exactly_one_default();

CREATE TABLE IF NOT EXISTS autocheck.action_dispatches (
    id             bigserial PRIMARY KEY,
    module         text NOT NULL,
    action         text NOT NULL,
    version        integer NOT NULL,
    request_id     text,
    correlation_id uuid NOT NULL,
    principal      text NOT NULL DEFAULT '',
    payload_hash   text NOT NULL,
    status         text NOT NULL DEFAULT 'OK'
                   CHECK (status IN ('OK', 'ERROR')),
    outcome        text,
    occurred_at    timestamptz NOT NULL DEFAULT clock_timestamp()
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
    status          text NOT NULL
                    CHECK (status IN ('CREATED', 'PROCESSING', 'COMPLETED', 'REJECTED')),
    process_id      uuid,
    amount          numeric(18,2),
    currency        text,
    payload         jsonb NOT NULL,
    payload_hash    text NOT NULL,
    outcome         text,
    result          jsonb,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (scope_key, idempotency_key)
);

CREATE TABLE IF NOT EXISTS autocheck.operation_events (
    event_id     uuid PRIMARY KEY,
    operation_id uuid NOT NULL
                 REFERENCES autocheck.operations (operation_id)
                 ON DELETE RESTRICT,
    event_type   text NOT NULL
                 CHECK (event_type IN (
                     'OPERATION_CREATED',
                     'OPERATION_COMPLETED',
                     'OPERATION_REJECTED'
                 )),
    payload_hash text NOT NULL,
    occurred_at  timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS autocheck.idempotency_claims (
    scope_key       text NOT NULL,
    idempotency_key text NOT NULL,
    payload_hash    text NOT NULL,
    status          text NOT NULL DEFAULT 'PENDING'
                    CHECK (status IN ('PENDING', 'COMPLETED', 'FAILED')),
    result          jsonb,
    claimed_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at    timestamptz,
    PRIMARY KEY (scope_key, idempotency_key)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON
    autocheck.contract_info,
    autocheck.action_definitions,
    autocheck.action_dispatches,
    autocheck.operations,
    autocheck.operation_events,
    autocheck.idempotency_claims
TO course_api;

GRANT USAGE, SELECT
ON ALL SEQUENCES IN SCHEMA autocheck
TO course_api;

GRANT SELECT, INSERT, UPDATE, DELETE ON
    autocheck.action_definitions,
    autocheck.action_dispatches,
    autocheck.operations,
    autocheck.operation_events,
    autocheck.idempotency_claims
TO api_owner;

GRANT USAGE, SELECT
ON ALL SEQUENCES IN SCHEMA autocheck
TO api_owner;

REVOKE ALL
ON autocheck.operations
FROM course_runtime;

REVOKE ALL
ON autocheck.operation_events
FROM course_runtime;

GRANT SELECT ON
    autocheck.contract_info,
    autocheck.action_definitions,
    autocheck.action_dispatches
TO course_runtime;

GRANT SELECT ON
    autocheck.operations,
    autocheck.operation_events,
    autocheck.idempotency_claims
TO course_runtime;

GRANT SELECT, INSERT, UPDATE, DELETE
ON autocheck.action_definitions
TO course_cli_login;

GRANT SELECT, INSERT, UPDATE, DELETE
ON autocheck.schema_migrations
TO course_cli_login, course_migration_login;

GRANT USAGE, SELECT
ON ALL SEQUENCES IN SCHEMA autocheck
TO course_cli_login;