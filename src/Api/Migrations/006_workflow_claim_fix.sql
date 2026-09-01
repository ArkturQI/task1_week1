-- ============================================================
-- Week 2 workflow claim/reclaim fix
-- ============================================================

CREATE OR REPLACE FUNCTION workflow.claim_jobs(
    p_owner text,
    p_limit integer DEFAULT 1,
    p_lease_seconds integer DEFAULT 2
)
RETURNS TABLE (
    job_id              uuid,
    process_id          uuid,
    step_instance_id    uuid,
    execution_id        uuid,
    lease_version       bigint,
    attempt_id          uuid,
    attempt_number      integer,
    lease_until         timestamptz,
    module              text,
    action              text,
    action_version      integer,
    required_policy     jsonb,
    timeout_ms          integer,
    retry_max_attempts  integer,
    retry_delays_ms     jsonb,
    input_mapping       jsonb,
    input_constants     jsonb,
    process_data        jsonb,
    flow_version_id     uuid,
    step_key            text,
    step_type           text,
    step_config         jsonb
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, public
AS $$
DECLARE
    v_job workflow.jobs%ROWTYPE;
    v_attempt workflow.task_attempts%ROWTYPE;
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

    -- ============================================================
    -- Reclaim expired leases
    -- ============================================================

    FOR v_job IN
        SELECT j.*
        FROM workflow.jobs AS j
        WHERE j.state = 'LEASED'
          AND j.lease_until IS NOT NULL
          AND j.lease_until <= v_now
        ORDER BY j.updated_at, j.job_id
        FOR UPDATE SKIP LOCKED
    LOOP

        UPDATE workflow.task_attempts AS ta
        SET status = 'STALE',
            finished_at = v_now
        WHERE ta.job_id = v_job.job_id
          AND ta.lease_version = v_job.lease_version
          AND ta.status = 'RUNNING';

        UPDATE workflow.jobs AS j
        SET state = 'READY',
            lease_owner = NULL,
            lease_until = NULL,
            lease_version = j.lease_version + 1,
            updated_at = v_now
        WHERE j.job_id = v_job.job_id;
    END LOOP;

    -- ============================================================
    -- Claim ready/retry jobs
    -- ============================================================

    FOR v_job IN
        SELECT j.*
        FROM workflow.jobs AS j
        WHERE j.state IN ('READY', 'RETRY_WAIT')
          AND j.next_attempt_at <= v_now
        ORDER BY j.next_attempt_at, j.created_at, j.job_id
        FOR UPDATE SKIP LOCKED
        LIMIT p_limit
    LOOP

        v_job.lease_version :=
            v_job.lease_version + 1;

        v_job.attempt_count :=
            v_job.attempt_count + 1;

        v_job.lease_until :=
            v_now + make_interval(
                secs => p_lease_seconds
            );

        UPDATE workflow.jobs AS j
        SET state = 'LEASED',
            lease_owner = p_owner,
            lease_version = v_job.lease_version,
            lease_until = v_job.lease_until,
            attempt_count = v_job.attempt_count,
            updated_at = v_now
        WHERE j.job_id = v_job.job_id;

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
        INTO v_attempt;

        RETURN QUERY
        SELECT
            v_job.job_id,
            v_job.process_id,
            v_job.step_instance_id,
            v_job.execution_id,
            v_job.lease_version,
            v_attempt.attempt_id,
            v_attempt.attempt_number,
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
        FROM workflow.jobs AS j
        JOIN workflow.process_instances AS p
          ON p.process_id = j.process_id
        JOIN workflow.step_instances AS si
          ON si.step_instance_id = j.step_instance_id
        JOIN workflow.step_definitions AS sd
          ON sd.flow_version_id = p.flow_version_id
         AND sd.step_key = si.step_key
        JOIN workflow.flow_versions AS fv
          ON fv.flow_version_id = p.flow_version_id
        LEFT JOIN workflow.task_definitions AS td
          ON td.step_definition_id = sd.step_definition_id
        WHERE j.job_id = v_job.job_id;

    END LOOP;
END;
$$;

ALTER FUNCTION workflow.claim_jobs(
    text,
    integer,
    integer
)
OWNER TO api_owner;

GRANT EXECUTE
ON FUNCTION workflow.claim_jobs(
    text,
    integer,
    integer
)
TO workflow_worker;