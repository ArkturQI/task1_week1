-- ============================================================
-- Week 2 API invoke fixes
-- ============================================================

-- Worker may call api.invoke through the API schema.
GRANT USAGE
ON SCHEMA api
TO workflow_worker;

CREATE OR REPLACE FUNCTION api.invoke(p_module text, p_action text, p_version integer, p_context jsonb, p_payload jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, autocheck, public AS $$
DECLARE
    v_def record; v_rec record; v_validation jsonb; v_result jsonb; v_final jsonb;
    v_key text; v_scope_key text; v_payload_hash text; v_stored jsonb; v_stored_hash text;
    v_claim_status text; v_correlation uuid; v_msg text; v_timeout_ms int;
    v_attempts int := 0; v_max_attempts int;
BEGIN
    -- 1. Version resolution
    IF p_version IS NOT NULL THEN
        SELECT * INTO v_def FROM autocheck.action_definitions d WHERE d.module = p_module AND d.action = p_action AND d.version = p_version AND d.enabled;
    ELSE
        SELECT * INTO v_def FROM autocheck.action_definitions d WHERE d.module = p_module AND d.action = p_action AND d.enabled AND d.is_default;
    END IF;
    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'action.not_found', 'message', 'action not found',
            'meta', jsonb_build_object('correlationId', coalesce(p_context ->> 'correlationId', gen_random_uuid()::text), 'actionVersion', p_version));
    END IF;

    -- 2. Enforce manifest timeout at DB level
    v_timeout_ms := coalesce((v_def.manifest ->> 'timeout_ms')::int, 10000);
    BEGIN EXECUTE format('SET LOCAL statement_timeout = %L', v_timeout_ms); EXCEPTION WHEN OTHERS THEN NULL; END;

    BEGIN v_correlation := (p_context ->> 'correlationId')::uuid; EXCEPTION WHEN OTHERS THEN v_correlation := gen_random_uuid(); END;

    -- 3. Policy check
    IF coalesce(jsonb_array_length(v_def.manifest -> 'required_policy'), 0) > 0 THEN
        FOR v_rec IN SELECT jsonb_array_elements_text(v_def.manifest -> 'required_policy') AS sc LOOP
            IF NOT coalesce(p_context -> 'scopes', '[]'::jsonb) @> jsonb_build_array(v_rec.sc) THEN
                RETURN jsonb_build_object('status', 'error', 'code', 'access.denied', 'message', 'missing required scope',
                    'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
            END IF;
        END LOOP;
    END IF;

    -- 4. Idempotency requirement check
    v_key := p_context ->> 'idempotencyKey';
    IF (v_def.manifest ->> 'idempotency_mode') = 'required' AND (v_key IS NULL OR v_key = '') THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.required', 'message', 'Idempotency-Key header is required',
            'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
    END IF;

    -- 5. Request schema validation
    v_validation := api.json_schema_validate(v_def.manifest -> 'request_schema', p_payload);
    IF NOT (v_validation ->> 'valid')::boolean THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', v_validation ->> 'error',
            'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
    END IF;

    v_payload_hash := encode(digest(convert_to(p_payload::text, 'UTF8'), 'sha256'), 'hex');

    CASE coalesce(v_def.manifest ->> 'idempotency_scope', 'principal_action')
        WHEN 'consumer_action' THEN v_scope_key := coalesce(p_context ->> 'consumer', 'anon') || '|' || p_module || '.' || p_action;
        WHEN 'global_action'   THEN v_scope_key := 'global|' || p_module || '.' || p_action;
        ELSE                        v_scope_key := coalesce(p_context ->> 'principal', 'anon') || '|' || p_module || '.' || p_action;
    END CASE;

    -- 6. ATOMIC PRE-EXECUTION CLAIM
    IF v_key IS NOT NULL AND v_key <> '' THEN
        v_max_attempts := GREATEST(1, v_timeout_ms / 50);
        LOOP
            INSERT INTO autocheck.idempotency_claims (scope_key, idempotency_key, payload_hash, status)
            VALUES (v_scope_key, v_key, v_payload_hash, 'PENDING')
            ON CONFLICT (scope_key, idempotency_key) DO NOTHING;

            IF FOUND THEN EXIT; END IF; -- winner proceeds to target

            SELECT result, payload_hash, status INTO v_stored, v_stored_hash, v_claim_status
            FROM autocheck.idempotency_claims
            WHERE scope_key = v_scope_key AND idempotency_key = v_key
            FOR UPDATE;

            IF NOT FOUND THEN
                v_attempts := v_attempts + 1;
                IF v_attempts > v_max_attempts THEN
                    RETURN jsonb_build_object('status', 'error', 'code', 'internal.error', 'message', 'idempotent request timeout', 'retryable', true,
                        'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
                END IF;
                PERFORM pg_sleep(0.05);
                CONTINUE;
            END IF;

            IF v_stored_hash <> v_payload_hash THEN
                RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.conflict', 'message', 'same key with different payload',
                    'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
            END IF;

            IF v_claim_status = 'COMPLETED' AND v_stored IS NOT NULL THEN
                INSERT INTO autocheck.action_dispatches (module, action, version, request_id, correlation_id, principal, payload_hash, status, outcome, occurred_at)
                VALUES (p_module, p_action, v_def.version, v_key, coalesce((v_stored -> 'meta' ->> 'correlationId')::uuid, v_correlation),
                        coalesce(p_context ->> 'principal', ''), v_payload_hash, 'OK', v_stored ->> 'outcome', clock_timestamp());
                RETURN v_stored;
            END IF;

            -- A previous invocation with the same idempotency key failed.
            -- Its claim is reusable: transition FAILED -> PENDING and retry
            -- the target execution with the same immutable payload hash.
            IF v_claim_status = 'FAILED' THEN
                UPDATE autocheck.idempotency_claims AS ic
                SET status = 'PENDING',
                    result = NULL,
                    claimed_at = clock_timestamp(),
                    completed_at = NULL
                WHERE ic.scope_key = v_scope_key
                  AND ic.idempotency_key = v_key
                  AND ic.payload_hash = v_payload_hash
                  AND ic.status = 'FAILED';

                IF FOUND THEN
                    EXIT;
                END IF;

                CONTINUE;
            END IF;

            v_attempts := v_attempts + 1;
            IF v_attempts > v_max_attempts THEN
                RETURN jsonb_build_object('status', 'error', 'code', 'internal.error', 'message', 'idempotent request is still in progress', 'retryable', true,
                    'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
            END IF;
            PERFORM pg_sleep(0.05);
        END LOOP;
    END IF;

    -- 7. Execute target function
    BEGIN
        EXECUTE format('SELECT %I.%I($1, $2)', v_def.target_schema, v_def.target_function)
        USING jsonb_set(p_context, '{correlationId}', to_jsonb(v_correlation::text)), p_payload INTO v_result;

        IF (v_result ->> 'status') = 'error' THEN RAISE EXCEPTION 'CTRL:%', v_result::text; END IF;
        IF NOT coalesce(v_def.outcomes, '[]'::jsonb) @> jsonb_build_array(v_result ->> 'outcome') THEN
            RAISE EXCEPTION 'CTRL:%', jsonb_build_object('status', 'error', 'code', 'action.contract_violation', 'message', 'undeclared outcome',
                'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version))::text;
        END IF;
        v_validation := api.json_schema_validate(v_def.manifest -> 'response_schema', v_result -> 'result');
        IF NOT (v_validation ->> 'valid')::boolean THEN
            RAISE EXCEPTION 'CTRL:%', jsonb_build_object('status', 'error', 'code', 'action.contract_violation', 'message', v_validation ->> 'error',
                'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version))::text;
        END IF;

        v_final := jsonb_set(
            jsonb_set(v_result, '{meta}', coalesce(v_result -> 'meta', '{}'::jsonb) || jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version)),
            '{status}', '"ok"'::jsonb);

        INSERT INTO autocheck.action_dispatches (module, action, version, request_id, correlation_id, principal, payload_hash, status, outcome, occurred_at)
        VALUES (p_module, p_action, v_def.version, coalesce(v_key, p_context ->> 'requestId', gen_random_uuid()::text), v_correlation,
                coalesce(p_context ->> 'principal', ''), v_payload_hash, 'OK', v_result ->> 'outcome', clock_timestamp());

        IF v_key IS NOT NULL AND v_key <> '' THEN
            UPDATE autocheck.idempotency_claims
            SET status = 'COMPLETED', result = v_final, completed_at = clock_timestamp()
            WHERE scope_key = v_scope_key AND idempotency_key = v_key;
        END IF;
    EXCEPTION WHEN OTHERS THEN
        GET STACKED DIAGNOSTICS v_msg = MESSAGE_TEXT;
        IF v_key IS NOT NULL AND v_key <> '' THEN
            IF v_msg LIKE 'CTRL:%' THEN
                UPDATE autocheck.idempotency_claims
                SET status = 'COMPLETED', result = substring(v_msg, 6)::jsonb, completed_at = clock_timestamp()
                WHERE scope_key = v_scope_key AND idempotency_key = v_key;
            ELSE
                UPDATE autocheck.idempotency_claims
                SET status = 'FAILED', result = jsonb_build_object('status','error','code','internal.error','message','internal error','retryable',true,'meta',jsonb_build_object('correlationId',v_correlation::text,'actionVersion',v_def.version)), completed_at = clock_timestamp()
                WHERE scope_key = v_scope_key AND idempotency_key = v_key;
            END IF;
        END IF;
        IF v_msg LIKE 'CTRL:%' THEN RETURN substring(v_msg, 6)::jsonb; END IF;
        RAISE LOG 'api.invoke failed correlation=% module=% action=% error=%', v_correlation::text, p_module, p_action, v_msg;
        RETURN jsonb_build_object('status', 'error', 'code', 'internal.error', 'message', 'internal error', 'retryable', true,
            'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
    END;
    RETURN v_final;
END; $$;
ALTER FUNCTION api.invoke OWNER TO api_owner;