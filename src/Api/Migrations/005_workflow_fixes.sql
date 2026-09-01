-- ============================================================
-- Week 2 workflow fixes
-- ============================================================

-- Worker must be able to reference api.invoke through the API schema.
GRANT USAGE
ON SCHEMA api
TO workflow_worker;

-- Normalize retry_delays_ms for definitions created by
-- older versions of the CLI.
--
-- Old representation:
-- {
--     "max_attempts": 3,
--     "delays_ms": [1000, 2000]
-- }
--
-- Required representation:
-- [
--     1000,
--     2000
-- ]
UPDATE workflow.task_definitions
SET retry_delays_ms =
    CASE
        WHEN jsonb_typeof(retry_delays_ms) = 'object'
             AND retry_delays_ms ? 'delays_ms'
        THEN retry_delays_ms -> 'delays_ms'
        ELSE retry_delays_ms
    END
WHERE jsonb_typeof(retry_delays_ms) = 'object';

-- Recreate fail_job with safe retry-delay handling.
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
    v_delays jsonb;
BEGIN
    SELECT *
    INTO v_job
    FROM workflow.jobs AS j
    WHERE j.job_id = p_job_id
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
    FROM workflow.process_instances AS p
    WHERE p.process_id = v_job.process_id
    FOR UPDATE;

    SELECT *
    INTO v_step
    FROM workflow.step_instances AS s
    WHERE s.step_instance_id = v_job.step_instance_id
    FOR UPDATE;

    SELECT *
    INTO v_attempt
    FROM workflow.task_attempts AS ta
    WHERE ta.job_id = v_job.job_id
      AND ta.lease_version = v_job.lease_version
      AND ta.status = 'RUNNING'
    ORDER BY ta.attempt_number DESC
    LIMIT 1
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION
            USING ERRCODE = 'P0001',
                  MESSAGE = 'workflow.lease_stale';
    END IF;

    SELECT td.*
    INTO v_task
    FROM workflow.task_definitions AS td
    JOIN workflow.step_definitions AS sd
      ON sd.step_definition_id = td.step_definition_id
    WHERE sd.flow_version_id = v_process.flow_version_id
      AND sd.step_key = v_step.step_key;

    UPDATE workflow.task_attempts AS ta
    SET status = 'FAILED',
        error_code = p_error_code,
        finished_at = clock_timestamp()
    WHERE ta.attempt_id = v_attempt.attempt_id;

    v_delays :=
        CASE
            WHEN jsonb_typeof(v_task.retry_delays_ms) = 'array'
            THEN v_task.retry_delays_ms
            WHEN jsonb_typeof(v_task.retry_delays_ms) = 'object'
                 AND v_task.retry_delays_ms ? 'delays_ms'
            THEN v_task.retry_delays_ms -> 'delays_ms'
            ELSE '[]'::jsonb
        END;

    IF p_retryable
       AND v_job.attempt_count < v_task.retry_max_attempts
    THEN
        IF v_job.attempt_count <= jsonb_array_length(v_delays)
        THEN
            v_delay_ms :=
                COALESCE(
                    (
                        v_delays ->
                        (v_job.attempt_count - 1)
                    )::text::integer,
                    0
                );
        END IF;

        v_next_attempt :=
            clock_timestamp()
            + make_interval(
                msecs => v_delay_ms
            );

        UPDATE workflow.jobs AS j
        SET state = 'RETRY_WAIT',
            lease_owner = NULL,
            lease_until = NULL,
            next_attempt_at = v_next_attempt,
            failure_count = j.failure_count + 1,
            updated_at = clock_timestamp()
        WHERE j.job_id = v_job.job_id;

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

    UPDATE workflow.jobs AS j
    SET state = 'DEAD',
        lease_owner = NULL,
        lease_until = NULL,
        failure_count = j.failure_count + 1,
        updated_at = clock_timestamp()
    WHERE j.job_id = v_job.job_id;

    UPDATE workflow.step_instances AS s
    SET state = 'FAILED',
        outcome = NULL,
        completed_at = clock_timestamp()
    WHERE s.step_instance_id = v_step.step_instance_id;

    UPDATE workflow.process_instances AS p
    SET state = 'FAILED',
        current_step_key = v_step.step_key,
        updated_at = clock_timestamp()
    WHERE p.process_id = v_process.process_id;

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