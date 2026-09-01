CREATE SCHEMA IF NOT EXISTS workflow;

-- ============================================================
-- Roles
-- ============================================================

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_roles
        WHERE rolname = 'workflow_worker'
    ) THEN
        CREATE ROLE workflow_worker NOLOGIN;
    END IF;
END
$$;

-- Worker должен видеть только schema metadata, но не иметь
-- прямого DML к workflow-таблицам.
GRANT USAGE
ON SCHEMA workflow
TO workflow_worker;

REVOKE ALL
ON ALL TABLES IN SCHEMA workflow
FROM workflow_worker;

REVOKE ALL
ON ALL SEQUENCES IN SCHEMA workflow
FROM workflow_worker;

REVOKE ALL
ON ALL FUNCTIONS IN SCHEMA workflow
FROM workflow_worker;

-- ============================================================
-- Flow definitions
-- ============================================================

CREATE TABLE IF NOT EXISTS workflow.flow_definitions (
    flow_id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_name       text NOT NULL UNIQUE,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS workflow.flow_versions (
    flow_version_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_id         uuid NOT NULL
                    REFERENCES workflow.flow_definitions(flow_id)
                    ON DELETE RESTRICT,
    flow_name       text NOT NULL,
    flow_version    integer NOT NULL,
    status          text NOT NULL
                    CHECK (status IN ('PUBLISHED')),
    is_active       boolean NOT NULL DEFAULT false,
    map             jsonb NOT NULL,
    published_at    timestamptz NOT NULL DEFAULT clock_timestamp(),

    UNIQUE (flow_name, flow_version),
    UNIQUE (flow_id, flow_version)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_flow_versions_active
    ON workflow.flow_versions(flow_name)
    WHERE is_active = true;

-- Immutable published definition:
-- existing version can only be inserted once.
CREATE OR REPLACE FUNCTION workflow.prevent_flow_version_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION
            USING ERRCODE = '55006',
                  MESSAGE = 'published workflow version is immutable';
    END IF;

    IF TG_OP = 'UPDATE' THEN
        IF OLD.flow_name <> NEW.flow_name
           OR OLD.flow_version <> NEW.flow_version
           OR OLD.map IS DISTINCT FROM NEW.map
           OR OLD.published_at <> NEW.published_at
           OR OLD.status <> NEW.status
        THEN
            RAISE EXCEPTION
                USING ERRCODE = '55006',
                      MESSAGE = 'published workflow version is immutable';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

ALTER FUNCTION workflow.prevent_flow_version_mutation()
OWNER TO api_owner;

DROP TRIGGER IF EXISTS trg_flow_versions_immutable
ON workflow.flow_versions;

CREATE TRIGGER trg_flow_versions_immutable
BEFORE UPDATE OR DELETE
ON workflow.flow_versions
FOR EACH ROW
EXECUTE FUNCTION workflow.prevent_flow_version_mutation();

-- ============================================================
-- Definition graph
-- ============================================================

CREATE TABLE IF NOT EXISTS workflow.step_definitions (
    step_definition_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_version_id    uuid NOT NULL
                       REFERENCES workflow.flow_versions(flow_version_id)
                       ON DELETE RESTRICT,
    step_key           text NOT NULL,
    step_type          text NOT NULL
                       CHECK (
                           step_type IN (
                               'automatic',
                               'wait_signal',
                               'manual',
                               'end'
                           )
                       ),
    step_config        jsonb NOT NULL,

    UNIQUE (flow_version_id, step_key)
);

CREATE TABLE IF NOT EXISTS workflow.task_definitions (
    task_definition_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    step_definition_id uuid NOT NULL UNIQUE
                       REFERENCES workflow.step_definitions(step_definition_id)
                       ON DELETE RESTRICT,

    service            text NOT NULL,
    module             text NOT NULL,
    action             text NOT NULL,
    action_version     integer NOT NULL,
    required_policy    jsonb NOT NULL,
    timeout_ms         integer NOT NULL,
    retry_max_attempts integer NOT NULL,
    retry_delays_ms    jsonb NOT NULL,
    input_mapping      jsonb NOT NULL,
    input_constants    jsonb NOT NULL
);

CREATE TABLE IF NOT EXISTS workflow.transition_definitions (
    transition_definition_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_version_id          uuid NOT NULL
                             REFERENCES workflow.flow_versions(flow_version_id)
                             ON DELETE RESTRICT,
    from_step_key            text NOT NULL,
    outcome                  text NOT NULL,
    to_step_key              text NOT NULL,

    UNIQUE (
        flow_version_id,
        from_step_key,
        outcome
    )
);

CREATE INDEX IF NOT EXISTS ix_transition_flow_from
    ON workflow.transition_definitions(
        flow_version_id,
        from_step_key
    );

-- ============================================================
-- Runtime
-- ============================================================

CREATE TABLE IF NOT EXISTS workflow.process_instances (
    process_id       uuid PRIMARY KEY,
    flow_id          uuid NOT NULL
                     REFERENCES workflow.flow_definitions(flow_id)
                     ON DELETE RESTRICT,
    flow_version_id  uuid NOT NULL
                     REFERENCES workflow.flow_versions(flow_version_id)
                     ON DELETE RESTRICT,
    flow_name        text NOT NULL,
    flow_version     integer NOT NULL,
    business_key     text NOT NULL,
    state            text NOT NULL
                     CHECK (
                         state IN (
                             'CREATED',
                             'RUNNING',
                             'WAITING_SIGNAL',
                             'WAITING_MANUAL',
                             'COMPLETED',
                             'FAILED'
                         )
                     ),
    current_step_key text,
    data             jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at       timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at       timestamptz NOT NULL DEFAULT clock_timestamp(),

    UNIQUE (flow_name, business_key)
);

CREATE INDEX IF NOT EXISTS ix_process_flow_version
    ON workflow.process_instances(flow_name, flow_version);

CREATE INDEX IF NOT EXISTS ix_process_state
    ON workflow.process_instances(state);

CREATE TABLE IF NOT EXISTS workflow.step_instances (
    step_instance_id uuid PRIMARY KEY,
    process_id       uuid NOT NULL
                     REFERENCES workflow.process_instances(process_id)
                     ON DELETE RESTRICT,
    step_key         text NOT NULL,
    step_type        text NOT NULL
                     CHECK (
                         step_type IN (
                             'AUTOMATIC',
                             'WAIT_SIGNAL',
                             'MANUAL',
                             'END'
                         )
                     ),
    state            text NOT NULL
                     CHECK (
                         state IN (
                             'PENDING',
                             'READY',
                             'RUNNING',
                             'WAITING',
                             'COMPLETED',
                             'FAILED'
                         )
                     ),
    outcome          text,
    entered_at       timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at     timestamptz
);

CREATE INDEX IF NOT EXISTS ix_step_instances_process
    ON workflow.step_instances(process_id, entered_at);

CREATE UNIQUE INDEX IF NOT EXISTS ux_step_instances_process_key
    ON workflow.step_instances(process_id, step_key);

CREATE TABLE IF NOT EXISTS workflow.jobs (
    job_id          uuid PRIMARY KEY,
    process_id      uuid NOT NULL
                    REFERENCES workflow.process_instances(process_id)
                    ON DELETE RESTRICT,
    step_instance_id uuid NOT NULL
                     REFERENCES workflow.step_instances(step_instance_id)
                     ON DELETE RESTRICT,
    execution_id    uuid NOT NULL,
    state           text NOT NULL
                    CHECK (
                        state IN (
                            'READY',
                            'LEASED',
                            'RETRY_WAIT',
                            'SUCCEEDED',
                            'DEAD'
                        )
                    ),
    lease_owner     text,
    lease_version   bigint NOT NULL DEFAULT 0,
    lease_until     timestamptz,
    attempt_count   integer NOT NULL DEFAULT 0,
    failure_count   integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at      timestamptz NOT NULL DEFAULT clock_timestamp(),

    UNIQUE (step_instance_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_execution_id
    ON workflow.jobs(execution_id);

CREATE INDEX IF NOT EXISTS ix_jobs_claim
    ON workflow.jobs(state, next_attempt_at);

CREATE INDEX IF NOT EXISTS ix_jobs_lease
    ON workflow.jobs(state, lease_until);

CREATE TABLE IF NOT EXISTS workflow.task_attempts (
    attempt_id     uuid PRIMARY KEY,
    job_id         uuid NOT NULL
                   REFERENCES workflow.jobs(job_id)
                   ON DELETE RESTRICT,
    execution_id   uuid NOT NULL,
    lease_version  bigint NOT NULL,
    attempt_number integer NOT NULL,
    status         text NOT NULL
                   CHECK (
                       status IN (
                           'RUNNING',
                           'SUCCEEDED',
                           'FAILED',
                           'STALE'
                       )
                   ),
    outcome        text,
    error_code     text,
    started_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
    finished_at    timestamptz
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_attempt_job_number
    ON workflow.task_attempts(job_id, attempt_number);

CREATE INDEX IF NOT EXISTS ix_attempts_execution
    ON workflow.task_attempts(execution_id, attempt_number);

CREATE TABLE IF NOT EXISTS workflow.signals (
    message_id   text PRIMARY KEY,
    process_id   uuid NOT NULL
                 REFERENCES workflow.process_instances(process_id)
                 ON DELETE RESTRICT,
    signal_type  text NOT NULL,
    body         jsonb NOT NULL,
    body_hash    text NOT NULL,
    status       text NOT NULL
                 CHECK (
                     status IN (
                         'ACCEPTED',
                         'APPLIED'
                     )
                 ),
    received_at  timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at   timestamptz
);

CREATE INDEX IF NOT EXISTS ix_signals_process
    ON workflow.signals(process_id, received_at);

CREATE TABLE IF NOT EXISTS workflow.events (
    event_id         uuid PRIMARY KEY,
    process_id       uuid NOT NULL
                     REFERENCES workflow.process_instances(process_id)
                     ON DELETE RESTRICT,
    step_instance_id uuid
                     REFERENCES workflow.step_instances(step_instance_id)
                     ON DELETE RESTRICT,
    event_type       text NOT NULL,
    payload          jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at      timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS ix_workflow_events_process
    ON workflow.events(process_id, occurred_at, event_id);

-- ============================================================
-- Immutable event history
-- ============================================================

CREATE OR REPLACE FUNCTION workflow.prevent_event_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION
        USING ERRCODE = '55006',
              MESSAGE = 'workflow event history is append-only';
END;
$$;

ALTER FUNCTION workflow.prevent_event_mutation()
OWNER TO api_owner;

DROP TRIGGER IF EXISTS trg_events_immutable
ON workflow.events;

CREATE TRIGGER trg_events_immutable
BEFORE UPDATE OR DELETE
ON workflow.events
FOR EACH ROW
EXECUTE FUNCTION workflow.prevent_event_mutation();

-- ============================================================
-- Stable evidence views
-- ============================================================

CREATE OR REPLACE VIEW autocheck.flow_versions AS
SELECT
    fv.flow_name,
    fv.flow_version,
    fv.status,
    fv.is_active,
    fv.published_at
FROM workflow.flow_versions fv;

CREATE OR REPLACE VIEW autocheck.processes AS
SELECT
    p.process_id,
    p.business_key,
    p.flow_name,
    p.flow_version,
    p.state,
    p.current_step_key,
    p.created_at,
    p.updated_at
FROM workflow.process_instances p;

CREATE OR REPLACE VIEW autocheck.steps AS
SELECT
    s.step_instance_id,
    s.process_id,
    s.step_key,
    s.step_type,
    s.state,
    s.outcome,
    s.entered_at,
    s.completed_at
FROM workflow.step_instances s;

CREATE OR REPLACE VIEW autocheck.jobs AS
SELECT
    j.job_id,
    j.process_id,
    j.step_instance_id,
    j.execution_id,
    j.state,
    j.lease_owner,
    j.lease_version,
    j.lease_until,
    j.attempt_count,
    j.next_attempt_at
FROM workflow.jobs j;

CREATE OR REPLACE VIEW autocheck.attempts AS
SELECT
    a.attempt_id,
    a.job_id,
    a.execution_id,
    a.lease_version,
    a.attempt_number,
    a.status,
    a.outcome,
    a.error_code,
    a.started_at,
    a.finished_at
FROM workflow.task_attempts a;

CREATE OR REPLACE VIEW autocheck.signals AS
SELECT
    s.message_id,
    s.process_id,
    s.signal_type,
    s.body_hash,
    s.status,
    s.received_at
FROM workflow.signals s;

CREATE OR REPLACE VIEW autocheck.workflow_events AS
SELECT
    e.event_id,
    e.process_id,
    e.step_instance_id,
    e.event_type,
    e.occurred_at
FROM workflow.events e;

-- Views are read-only evidence.
GRANT SELECT
ON autocheck.flow_versions,
   autocheck.processes,
   autocheck.steps,
   autocheck.jobs,
   autocheck.attempts,
   autocheck.signals,
   autocheck.workflow_events
TO course_api_login,
   course_cli_login,
   course_migration_login;

-- ============================================================
-- Worker boundary: claim_jobs
-- ============================================================

CREATE OR REPLACE FUNCTION workflow.claim_jobs(
    p_owner text,
    p_limit integer DEFAULT 1,
    p_lease_seconds integer DEFAULT 2
)
RETURNS TABLE (
    job_id           uuid,
    process_id       uuid,
    step_instance_id uuid,
    execution_id     uuid,
    lease_version    bigint,
    attempt_id       uuid,
    attempt_number   integer,
    lease_until      timestamptz,
    module           text,
    action           text,
    action_version   integer,
    required_policy  jsonb,
    timeout_ms       integer,
    retry_max_attempts integer,
    retry_delays_ms  jsonb,
    input_mapping    jsonb,
    input_constants  jsonb,
    process_data     jsonb,
    flow_version_id  uuid,
    step_key         text,
    step_type        text,
    step_config      jsonb
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, public
AS $$
DECLARE
    v_job workflow.jobs%ROWTYPE;
    v_old_attempt workflow.task_attempts%ROWTYPE;
    v_now timestamptz := clock_timestamp();
BEGIN
    IF p_owner IS NULL OR btrim(p_owner) = '' THEN
        RAISE EXCEPTION
            USING ERRCODE = '22023',
                  MESSAGE = 'worker owner is required';
    END IF;

    IF p_limit < 1 OR p_limit > 32 THEN
        RAISE EXCEPTION
            USING ERRCODE = '22023',
                  MESSAGE = 'invalid claim limit';
    END IF;

    IF p_lease_seconds < 1 OR p_lease_seconds > 300 THEN
        RAISE EXCEPTION
            USING ERRCODE = '22023',
                  MESSAGE = 'invalid lease duration';
    END IF;

    -- Reclaim expired leases first.
    FOR v_job IN
        SELECT j.*
        FROM workflow.jobs j
        WHERE j.state = 'LEASED'
          AND j.lease_until IS NOT NULL
          AND j.lease_until <= v_now
        ORDER BY j.updated_at, j.job_id
        FOR UPDATE SKIP LOCKED
    LOOP
        UPDATE workflow.task_attempts
        SET status = 'STALE',
            finished_at = v_now
        WHERE job_id = v_job.job_id
          AND lease_version = v_job.lease_version
          AND status = 'RUNNING';

        UPDATE workflow.jobs
        SET state = 'READY',
            lease_owner = NULL,
            lease_until = NULL,
            lease_version = lease_version + 1,
            updated_at = v_now
        WHERE job_id = v_job.job_id;
    END LOOP;

    FOR v_job IN
        SELECT j.*
        FROM workflow.jobs j
        WHERE j.state IN ('READY', 'RETRY_WAIT')
          AND j.next_attempt_at <= v_now
        ORDER BY j.next_attempt_at, j.created_at, j.job_id
        FOR UPDATE SKIP LOCKED
        LIMIT p_limit
    LOOP
        v_job.lease_version := v_job.lease_version + 1;
        v_job.attempt_count := v_job.attempt_count + 1;
        v_job.lease_until :=
            v_now + make_interval(secs => p_lease_seconds);

        UPDATE workflow.jobs
        SET state = 'LEASED',
            lease_owner = p_owner,
            lease_version = v_job.lease_version,
            lease_until = v_job.lease_until,
            attempt_count = v_job.attempt_count,
            updated_at = v_now
        WHERE workflow.jobs.job_id = v_job.job_id;

        INSERT INTO workflow.task_attempts (
            attempt_id,
            job_id,
            execution_id,
            lease_version,
            attempt_number,
            status
        )
        VALUES (
            gen_random_uuid(),
            v_job.job_id,
            v_job.execution_id,
            v_job.lease_version,
            v_job.attempt_count,
            'RUNNING'
        )
        RETURNING *
        INTO v_old_attempt;

        RETURN QUERY
        SELECT
            v_job.job_id,
            v_job.process_id,
            v_job.step_instance_id,
            v_job.execution_id,
            v_job.lease_version,
            v_old_attempt.attempt_id,
            v_old_attempt.attempt_number,
            v_job.lease_until,
            td.module,
            td.action,
            td.action_version,
            td.required_policy,
            td.timeout_ms,
            td.retry_max_attempts,
            td.retry_delays_ms,
            td.input_mapping,
            td.input_constants,
            p.data,
            fv.flow_version_id,
            si.step_key,
            si.step_type,
            sd.step_config
        FROM workflow.jobs j
        JOIN workflow.process_instances p
          ON p.process_id = j.process_id
        JOIN workflow.step_instances si
          ON si.step_instance_id = j.step_instance_id
        JOIN workflow.step_definitions sd
          ON sd.flow_version_id = p.flow_version_id
         AND sd.step_key = si.step_key
        JOIN workflow.flow_versions fv
          ON fv.flow_version_id = p.flow_version_id
        LEFT JOIN workflow.task_definitions td
          ON td.step_definition_id = sd.step_definition_id
        WHERE j.job_id = v_job.job_id;
    END LOOP;
END;
$$;

ALTER FUNCTION workflow.claim_jobs(text, integer, integer)
OWNER TO api_owner;

-- ============================================================
-- Worker boundary: finish_job
-- ============================================================

CREATE OR REPLACE FUNCTION workflow.finish_job(
    p_job_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_outcome text,
    p_result jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, public
AS $$
DECLARE
    v_job workflow.jobs%ROWTYPE;
    v_process workflow.process_instances%ROWTYPE;
    v_step workflow.step_instances%ROWTYPE;
    v_next_step workflow.step_definitions%ROWTYPE;
    v_next_instance workflow.step_instances%ROWTYPE;
    v_next_task workflow.task_definitions%ROWTYPE;
    v_attempt workflow.task_attempts%ROWTYPE;
    v_transition workflow.transition_definitions%ROWTYPE;
    v_event_type text := 'TaskCompleted';
BEGIN
    SELECT *
    INTO v_job
    FROM workflow.jobs
    WHERE job_id = p_job_id
    FOR UPDATE;

    IF NOT FOUND
       OR v_job.state <> 'LEASED'
       OR v_job.lease_owner IS DISTINCT FROM p_owner
       OR v_job.lease_version <> p_lease_version
       OR v_job.lease_until IS NULL
       OR v_job.lease_until <= clock_timestamp()
    THEN
        RAISE EXCEPTION
            USING ERRCODE = 'P0001',
                  MESSAGE = 'workflow.lease_stale';
    END IF;

    SELECT *
    INTO v_process
    FROM workflow.process_instances
    WHERE process_id = v_job.process_id
    FOR UPDATE;

    SELECT *
    INTO v_step
    FROM workflow.step_instances
    WHERE step_instance_id = v_job.step_instance_id
    FOR UPDATE;

    SELECT *
    INTO v_attempt
    FROM workflow.task_attempts
    WHERE job_id = v_job.job_id
      AND lease_version = v_job.lease_version
      AND status = 'RUNNING'
    ORDER BY attempt_number DESC
    LIMIT 1
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION
            USING ERRCODE = 'P0001',
                  MESSAGE = 'workflow.lease_stale';
    END IF;

    SELECT *
    INTO v_transition
    FROM workflow.transition_definitions
    WHERE flow_version_id = v_process.flow_version_id
      AND from_step_key = v_step.step_key
      AND outcome = p_outcome;

    IF NOT FOUND THEN
        RAISE EXCEPTION
            USING ERRCODE = 'P0001',
                  MESSAGE = 'workflow.unknown_outcome';
    END IF;

    UPDATE workflow.task_attempts
    SET status = 'SUCCEEDED',
        outcome = p_outcome,
        finished_at = clock_timestamp()
    WHERE attempt_id = v_attempt.attempt_id;

    UPDATE workflow.jobs
    SET state = 'SUCCEEDED',
        lease_owner = NULL,
        lease_until = NULL,
        updated_at = clock_timestamp()
    WHERE job_id = v_job.job_id;

    UPDATE workflow.step_instances
    SET state = 'COMPLETED',
        outcome = p_outcome,
        completed_at = clock_timestamp()
    WHERE step_instance_id = v_step.step_instance_id;

    INSERT INTO workflow.events (
        event_id,
        process_id,
        step_instance_id,
        event_type,
        payload
    )
    VALUES (
        gen_random_uuid(),
        v_process.process_id,
        v_step.step_instance_id,
        v_event_type,
        jsonb_build_object(
            'outcome', p_outcome,
            'result', COALESCE(p_result, '{}'::jsonb),
            'executionId', v_job.execution_id,
            'attemptId', v_attempt.attempt_id,
            'leaseVersion', v_job.lease_version
        )
    );

    SELECT sd.*
    INTO v_next_step
    FROM workflow.step_definitions sd
    WHERE sd.flow_version_id = v_process.flow_version_id
      AND sd.step_key = v_transition.to_step_key;

    IF NOT FOUND THEN
        RAISE EXCEPTION
            USING ERRCODE = 'P0001',
                  MESSAGE = 'workflow.next_step_not_found';
    END IF;

    INSERT INTO workflow.step_instances (
        step_instance_id,
        process_id,
        step_key,
        step_type,
        state
    )
    VALUES (
        gen_random_uuid(),
        v_process.process_id,
        v_next_step.step_key,
        upper(v_next_step.step_type),
        CASE
            WHEN v_next_step.step_type = 'automatic' THEN 'READY'
            WHEN v_next_step.step_type = 'end' THEN 'COMPLETED'
            ELSE 'WAITING'
        END
    )
    RETURNING *
    INTO v_next_instance;

    IF v_next_step.step_type = 'end' THEN

        UPDATE workflow.process_instances
        SET state = 'COMPLETED',
            current_step_key = v_next_step.step_key,
            updated_at = clock_timestamp()
        WHERE process_id = v_process.process_id;

        INSERT INTO workflow.events (
            event_id,
            process_id,
            step_instance_id,
            event_type,
            payload
        )
        VALUES (
            gen_random_uuid(),
            v_process.process_id,
            v_next_instance.step_instance_id,
            'ProcessCompleted',
            jsonb_build_object(
                'outcome', v_next_step.step_config ->> 'outcome'
            )
        );

        RETURN jsonb_build_object(
            'status', 'ok',
            'operation', 'finished',
            'jobId', p_job_id,
            'processId', v_process.process_id,
            'nextStepKey', v_next_step.step_key,
            'state', 'COMPLETED'
        );

    ELSIF v_next_step.step_type = 'wait_signal' THEN

        UPDATE workflow.process_instances
        SET state = 'WAITING_SIGNAL',
            current_step_key = v_next_step.step_key,
            updated_at = clock_timestamp()
        WHERE process_id = v_process.process_id;

        INSERT INTO workflow.events (
            event_id,
            process_id,
            step_instance_id,
            event_type,
            payload
        )
        VALUES (
            gen_random_uuid(),
            v_process.process_id,
            v_next_instance.step_instance_id,
            'StepWaiting',
            jsonb_build_object(
                'type', 'wait_signal',
                'signalType',
                    v_next_step.step_config ->> 'signal_type'
            )
        );

        RETURN jsonb_build_object(
            'status', 'ok',
            'operation', 'finished',
            'jobId', p_job_id,
            'processId', v_process.process_id,
            'nextStepKey', v_next_step.step_key,
            'state', 'WAITING_SIGNAL'
        );

    ELSIF v_next_step.step_type = 'manual' THEN

        UPDATE workflow.process_instances
        SET state = 'WAITING_MANUAL',
            current_step_key = v_next_step.step_key,
            updated_at = clock_timestamp()
        WHERE process_id = v_process.process_id;

        INSERT INTO workflow.events (
            event_id,
            process_id,
            step_instance_id,
            event_type,
            payload
        )
        VALUES (
            gen_random_uuid(),
            v_process.process_id,
            v_next_instance.step_instance_id,
            'StepWaiting',
            jsonb_build_object(
                'type', 'manual',
                'allowedOutcomes',
                    v_next_step.step_config -> 'allowed_outcomes'
            )
        );

        RETURN jsonb_build_object(
            'status', 'ok',
            'operation', 'finished',
            'jobId', p_job_id,
            'processId', v_process.process_id,
            'nextStepKey', v_next_step.step_key,
            'state', 'WAITING_MANUAL'
        );

    ELSIF v_next_step.step_type = 'automatic' THEN

        SELECT td.*
        INTO v_next_task
        FROM workflow.task_definitions td
        WHERE td.step_definition_id = v_next_step.step_definition_id;

        UPDATE workflow.process_instances
        SET state = 'RUNNING',
            current_step_key = v_next_step.step_key,
            updated_at = clock_timestamp()
        WHERE process_id = v_process.process_id;

        INSERT INTO workflow.jobs (
            job_id,
            process_id,
            step_instance_id,
            execution_id,
            state,
            next_attempt_at
        )
        VALUES (
            gen_random_uuid(),
            v_process.process_id,
            v_next_instance.step_instance_id,
            gen_random_uuid(),
            'READY',
            clock_timestamp()
        );

        INSERT INTO workflow.events (
            event_id,
            process_id,
            step_instance_id,
            event_type,
            payload
        )
        VALUES (
            gen_random_uuid(),
            v_process.process_id,
            v_next_instance.step_instance_id,
            'JobReady',
            jsonb_build_object(
                'module', v_next_task.module,
                'action', v_next_task.action,
                'actionVersion', v_next_task.action_version
            )
        );

        RETURN jsonb_build_object(
            'status', 'ok',
            'operation', 'finished',
            'jobId', p_job_id,
            'processId', v_process.process_id,
            'nextStepKey', v_next_step.step_key,
            'state', 'RUNNING'
        );
    END IF;

    RAISE EXCEPTION
        USING ERRCODE = 'P0001',
              MESSAGE = 'workflow.unsupported_step_type';
END;
$$;

ALTER FUNCTION workflow.finish_job(
    uuid,
    text,
    bigint,
    text,
    jsonb
)
OWNER TO api_owner;

-- ============================================================
-- Worker boundary: fail_job
-- ============================================================

CREATE OR REPLACE FUNCTION workflow.fail_job(
    p_job_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_error_code text,
    p_retryable boolean
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, public
AS $$
DECLARE
    v_job workflow.jobs%ROWTYPE;
    v_process workflow.process_instances%ROWTYPE;
    v_step workflow.step_instances%ROWTYPE;
    v_attempt workflow.task_attempts%ROWTYPE;
    v_task workflow.task_definitions%ROWTYPE;
    v_delay_ms integer := 0;
    v_next_attempt timestamptz;
BEGIN
    SELECT *
    INTO v_job
    FROM workflow.jobs
    WHERE job_id = p_job_id
    FOR UPDATE;

    IF NOT FOUND
       OR v_job.state <> 'LEASED'
       OR v_job.lease_owner IS DISTINCT FROM p_owner
       OR v_job.lease_version <> p_lease_version
       OR v_job.lease_until IS NULL
       OR v_job.lease_until <= clock_timestamp()
    THEN
        RAISE EXCEPTION
            USING ERRCODE = 'P0001',
                  MESSAGE = 'workflow.lease_stale';
    END IF;

    SELECT *
    INTO v_process
    FROM workflow.process_instances
    WHERE process_id = v_job.process_id
    FOR UPDATE;

    SELECT *
    INTO v_step
    FROM workflow.step_instances
    WHERE step_instance_id = v_job.step_instance_id
    FOR UPDATE;

    SELECT *
    INTO v_attempt
    FROM workflow.task_attempts
    WHERE job_id = v_job.job_id
      AND lease_version = v_job.lease_version
      AND status = 'RUNNING'
    ORDER BY attempt_number DESC
    LIMIT 1
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION
            USING ERRCODE = 'P0001',
                  MESSAGE = 'workflow.lease_stale';
    END IF;

    SELECT td.*
    INTO v_task
    FROM workflow.task_definitions td
    JOIN workflow.step_definitions sd
      ON sd.step_definition_id = td.step_definition_id
    WHERE sd.flow_version_id = v_process.flow_version_id
      AND sd.step_key = v_step.step_key;

    UPDATE workflow.task_attempts
    SET status = 'FAILED',
        error_code = p_error_code,
        finished_at = clock_timestamp()
    WHERE attempt_id = v_attempt.attempt_id;

    IF p_retryable
       AND v_job.attempt_count < v_task.retry_max_attempts
    THEN
        IF v_job.attempt_count <= jsonb_array_length(v_task.retry_delays_ms) THEN
            v_delay_ms :=
                COALESCE(
                    (v_task.retry_delays_ms ->> (v_job.attempt_count - 1))::integer,
                    0
                );
        END IF;

        v_next_attempt :=
            clock_timestamp()
            + make_interval(msecs => v_delay_ms);

        UPDATE workflow.jobs
        SET state = 'RETRY_WAIT',
            lease_owner = NULL,
            lease_until = NULL,
            next_attempt_at = v_next_attempt,
            failure_count = failure_count + 1,
            updated_at = clock_timestamp()
        WHERE job_id = v_job.job_id;

        INSERT INTO workflow.events (
            event_id,
            process_id,
            step_instance_id,
            event_type,
            payload
        )
        VALUES (
            gen_random_uuid(),
            v_process.process_id,
            v_step.step_instance_id,
            'TaskFailed',
            jsonb_build_object(
                'errorCode', p_error_code,
                'retryable', true,
                'attemptNumber', v_job.attempt_count,
                'nextAttemptAt', v_next_attempt
            )
        );

        RETURN jsonb_build_object(
            'status', 'ok',
            'operation', 'retry_scheduled',
            'jobId', p_job_id,
            'attemptNumber', v_job.attempt_count,
            'nextAttemptAt', v_next_attempt
        );
    END IF;

    UPDATE workflow.jobs
    SET state = 'DEAD',
        lease_owner = NULL,
        lease_until = NULL,
        failure_count = failure_count + 1,
        updated_at = clock_timestamp()
    WHERE job_id = v_job.job_id;

    UPDATE workflow.step_instances
    SET state = 'FAILED',
        outcome = NULL,
        completed_at = clock_timestamp()
    WHERE step_instance_id = v_step.step_instance_id;

    UPDATE workflow.process_instances
    SET state = 'FAILED',
        current_step_key = v_step.step_key,
        updated_at = clock_timestamp()
    WHERE process_id = v_process.process_id;

    INSERT INTO workflow.events (
        event_id,
        process_id,
        step_instance_id,
        event_type,
        payload
    )
    VALUES (
        gen_random_uuid(),
        v_process.process_id,
        v_step.step_instance_id,
        'TaskFailed',
        jsonb_build_object(
            'errorCode', p_error_code,
            'retryable', false,
            'attemptNumber', v_job.attempt_count
        )
    );

    RETURN jsonb_build_object(
        'status', 'ok',
        'operation', 'dead',
        'jobId', p_job_id,
        'state', 'DEAD'
    );
END;
$$;

ALTER FUNCTION workflow.fail_job(
    uuid,
    text,
    bigint,
    text,
    boolean
)
OWNER TO api_owner;

-- ============================================================
-- Worker privileges
-- ============================================================

GRANT EXECUTE
ON FUNCTION workflow.claim_jobs(
    text,
    integer,
    integer
)
TO workflow_worker;

GRANT EXECUTE
ON FUNCTION workflow.finish_job(
    uuid,
    text,
    bigint,
    text,
    jsonb
)
TO workflow_worker;

GRANT EXECUTE
ON FUNCTION workflow.fail_job(
    uuid,
    text,
    bigint,
    text,
    boolean
)
TO workflow_worker;

-- Worker must be able to call shared action runtime.
GRANT EXECUTE
ON FUNCTION api.invoke(
    text,
    text,
    integer,
    jsonb,
    jsonb
)
TO workflow_worker;

-- Explicitly deny generic table DML.
REVOKE INSERT, UPDATE, DELETE
ON ALL TABLES IN SCHEMA workflow
FROM workflow_worker;

-- ============================================================
-- API / CLI access to workflow evidence
-- ============================================================

GRANT SELECT
ON workflow.flow_definitions,
   workflow.flow_versions,
   workflow.step_definitions,
   workflow.task_definitions,
   workflow.transition_definitions
TO course_api_login,
   course_cli_login,
   course_migration_login;

GRANT SELECT
ON workflow.process_instances,
   workflow.step_instances,
   workflow.jobs,
   workflow.task_attempts,
   workflow.signals,
   workflow.events
TO course_api_login,
   course_cli_login,
   course_migration_login;

-- ============================================================
-- Workflow schema ownership
-- ============================================================

ALTER TABLE workflow.flow_definitions
    OWNER TO api_owner;

ALTER TABLE workflow.flow_versions
    OWNER TO api_owner;

ALTER TABLE workflow.step_definitions
    OWNER TO api_owner;

ALTER TABLE workflow.task_definitions
    OWNER TO api_owner;

ALTER TABLE workflow.transition_definitions
    OWNER TO api_owner;

ALTER TABLE workflow.process_instances
    OWNER TO api_owner;

ALTER TABLE workflow.step_instances
    OWNER TO api_owner;

ALTER TABLE workflow.jobs
    OWNER TO api_owner;

ALTER TABLE workflow.task_attempts
    OWNER TO api_owner;

ALTER TABLE workflow.signals
    OWNER TO api_owner;

ALTER TABLE workflow.events
    OWNER TO api_owner;

-- ============================================================
-- Search path safety
-- ============================================================

REVOKE CREATE
ON SCHEMA workflow
FROM PUBLIC;

-- ============================================================
-- CLI workflow publication privileges
-- ============================================================

GRANT SELECT, INSERT
ON workflow.flow_definitions,
   workflow.flow_versions,
   workflow.step_definitions,
   workflow.task_definitions,
   workflow.transition_definitions
TO course_cli_login;

GRANT UPDATE
ON workflow.flow_versions
TO course_cli_login;

GRANT SELECT
ON workflow.process_instances,
   workflow.step_instances,
   workflow.jobs,
   workflow.task_attempts,
   workflow.signals,
   workflow.events
TO course_cli_login;