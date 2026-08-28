CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- =====================================================================
-- Schema and role bootstrap.
--
-- IDENTITY MODEL:
--   course_migration_login  — DDL/bootstrap only (runs once at container start)
--   course_api_login        — API runtime (EXECUTE on api.* functions)
--   course_cli_login        — publication CLI (DML on autocheck catalog)
--   api_owner               — NOLOGIN NOSUPERUSER owner of SECURITY DEFINER functions
--   course_api              — NOLOGIN publication role (inherited by CLI)
--   course_runtime          — NOLOGIN read-only role (inherited by API)
--
-- SECURITY DEFINER functions are owned by api_owner (not superuser) to enforce
-- least privilege: runtime callers can only EXECUTE, never touch tables directly.
-- =====================================================================
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
    -- api_owner owns all SECURITY DEFINER functions; NOLOGIN prevents direct connection
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'api_owner') THEN
        CREATE ROLE api_owner NOLOGIN NOSUPERUSER;
    END IF;
    -- Bootstrap role with CREATEROLE so it can create other roles and transfer ownership
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_migration_login') THEN
        CREATE ROLE course_migration_login WITH LOGIN PASSWORD 'migration_secret_change_me' CREATEROLE;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_api_login') THEN
        CREATE ROLE course_api_login WITH LOGIN PASSWORD 'api_secret_change_me';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'course_cli_login') THEN
        CREATE ROLE course_cli_login WITH LOGIN PASSWORD 'cli_secret_change_me';
    END IF;
END $$;

-- Migration role needs database-level privileges to bootstrap schemas and transfer ownership
GRANT CONNECT, CREATE ON DATABASE course TO course_migration_login;
GRANT api_owner TO course_migration_login;
GRANT CREATE ON SCHEMA api TO api_owner;
GRANT CREATE ON SCHEMA opencheck TO course_cli_login;

-- Role inheritance: login roles inherit privileges of group roles
GRANT course_runtime TO course_api_login;
GRANT course_api TO course_cli_login;

GRANT USAGE ON SCHEMA autocheck TO course_runtime, course_api, api_owner, course_api_login, course_cli_login, course_migration_login;
GRANT USAGE ON SCHEMA api       TO course_runtime, course_api, api_owner, course_api_login, course_cli_login, course_migration_login;
GRANT USAGE ON SCHEMA opencheck TO api_owner, course_runtime, course_cli_login, course_migration_login;

-- Contract metadata (version, generation timestamp)
CREATE TABLE IF NOT EXISTS autocheck.contract_info (
    contract_version text PRIMARY KEY,
    generated_at     timestamptz NOT NULL DEFAULT now()
);
INSERT INTO autocheck.contract_info (contract_version) VALUES ('course-1') ON CONFLICT DO NOTHING;

-- Migration tracking (filename, checksum, applied_at)
CREATE TABLE IF NOT EXISTS autocheck.schema_migrations (
    file_name  text PRIMARY KEY,
    checksum   text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);

-- Action catalog: published manifest + routing metadata.
-- UNIQUE(module, action, version) prevents duplicate versions.
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

-- Exactly-one default per (module, action) enforced at database level.
-- Partial unique index: only one row per (module, action) can have is_default=true.
CREATE UNIQUE INDEX IF NOT EXISTS idx_action_default_unique
    ON autocheck.action_definitions (module, action)
    WHERE is_default = true;

-- Dispatch log: one row per api.invoke call (observability / audit).
-- CHECK constraints enforce valid status values.
CREATE TABLE IF NOT EXISTS autocheck.action_dispatches (
    id             bigserial PRIMARY KEY,
    module         text NOT NULL,
    action         text NOT NULL,
    version        integer NOT NULL,
    request_id     text,
    correlation_id uuid NOT NULL,
    principal      text NOT NULL DEFAULT '',
    payload_hash   text NOT NULL,
    status         text NOT NULL DEFAULT 'OK' CHECK (status IN ('OK', 'ERROR')),
    outcome        text,
    occurred_at    timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS ix_dispatches_route ON autocheck.action_dispatches (module, action);
CREATE INDEX IF NOT EXISTS ix_dispatches_request ON autocheck.action_dispatches (request_id);

-- Domain operations: payment-specific projection.
-- Separate from idempotency_claims (technical dedup) to maintain independent invariants.
-- CHECK constraint enforces valid state machine transitions.
CREATE TABLE IF NOT EXISTS autocheck.operations (
    operation_id    uuid PRIMARY KEY,
    request_id      text NOT NULL,
    idempotency_key text NOT NULL,
    scope_key       text NOT NULL,
    module          text NOT NULL,
    action          text NOT NULL,
    version         integer NOT NULL,
    operation_kind  text,
    status          text NOT NULL CHECK (status IN ('CREATED', 'PROCESSING', 'COMPLETED', 'REJECTED')),
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

-- Domain events: one event per operation state transition.
-- FK to operations with ON DELETE RESTRICT prevents orphaned events.
-- CHECK constraint enforces valid event types.
CREATE TABLE IF NOT EXISTS autocheck.operation_events (
    event_id     uuid PRIMARY KEY,
    operation_id uuid NOT NULL REFERENCES autocheck.operations (operation_id) ON DELETE RESTRICT,
    event_type   text NOT NULL CHECK (event_type IN ('OPERATION_CREATED', 'OPERATION_COMPLETED', 'OPERATION_REJECTED')),
    payload_hash text NOT NULL,
    occurred_at  timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- Technical idempotency storage: atomic claim before target execution.
-- Separated from operations to keep generic idempotency independent of domain model.
-- CHECK constraint enforces valid claim states.
CREATE TABLE IF NOT EXISTS autocheck.idempotency_claims (
    scope_key       text NOT NULL,
    idempotency_key text NOT NULL,
    payload_hash    text NOT NULL,
    status          text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING', 'COMPLETED', 'FAILED')),
    result          jsonb,
    claimed_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at    timestamptz,
    PRIMARY KEY (scope_key, idempotency_key)
);

-- =====================================================================
-- GRANT PHILOSOPHY:
--   course_api (publication): full DML on catalog + runtime tables
--   api_owner (SECURITY DEFINER owner): DML on runtime tables only
--   course_runtime: SELECT only on projections, no mutations
--   course_cli_login: catalog DML + migration tracking
--   Login roles (api_login, cli_login, migration_login): only EXECUTE on functions
-- =====================================================================

-- Publication role: full catalog and runtime DML
GRANT SELECT, INSERT, UPDATE, DELETE ON
    autocheck.contract_info,
    autocheck.action_definitions,
    autocheck.action_dispatches,
    autocheck.operations,
    autocheck.operation_events,
    autocheck.idempotency_claims
TO course_api;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA autocheck TO course_api;

-- api_owner: DML on runtime tables (used by SECURITY DEFINER functions)
GRANT SELECT, INSERT, UPDATE, DELETE ON
    autocheck.action_definitions,
    autocheck.action_dispatches,
    autocheck.operations,
    autocheck.operation_events,
    autocheck.idempotency_claims
TO api_owner;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA autocheck TO api_owner;

-- course_runtime: read-only access to projections, no mutations
REVOKE ALL ON autocheck.operations FROM course_runtime;
REVOKE ALL ON autocheck.operation_events FROM course_runtime;
GRANT SELECT ON autocheck.contract_info, autocheck.action_definitions, autocheck.action_dispatches TO course_runtime;
GRANT SELECT ON autocheck.operations, autocheck.operation_events, autocheck.idempotency_claims TO course_runtime;

-- CLI login: catalog DML + migration tracking
GRANT SELECT, INSERT, UPDATE, DELETE ON autocheck.action_definitions TO course_cli_login;
GRANT SELECT, INSERT, UPDATE, DELETE ON autocheck.schema_migrations TO course_cli_login, course_migration_login;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA autocheck TO course_cli_login;