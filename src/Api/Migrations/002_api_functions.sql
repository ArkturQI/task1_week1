CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE OR REPLACE FUNCTION api.json_schema_validate(p_schema jsonb, p_data jsonb)
RETURNS jsonb
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    v_type text;
    v_key text;
    v_prop jsonb;
    v_r jsonb;
    v_text text;
BEGIN
    IF p_schema IS NULL OR p_data IS NULL THEN
        RETURN jsonb_build_object('valid', false, 'error', 'schema or data is null');
    END IF;

    v_type := p_schema ->> 'type';
    IF v_type = 'object' AND jsonb_typeof(p_data) <> 'object' THEN
        RETURN jsonb_build_object('valid', false, 'error', 'expected object');
    END IF;
    IF v_type = 'string' AND jsonb_typeof(p_data) <> 'string' THEN
        RETURN jsonb_build_object('valid', false, 'error', 'expected string');
    END IF;
    IF v_type = 'boolean' AND jsonb_typeof(p_data) <> 'boolean' THEN
        RETURN jsonb_build_object('valid', false, 'error', 'expected boolean');
    END IF;
    IF v_type IN ('number', 'integer') AND jsonb_typeof(p_data) NOT IN ('number', 'integer') THEN
        RETURN jsonb_build_object('valid', false, 'error', 'expected number');
    END IF;

    IF jsonb_typeof(p_data) = 'string' THEN
        v_text := p_data #>> '{}';
        IF p_schema ? 'minLength' AND length(v_text) < (p_schema ->> 'minLength')::int THEN
            RETURN jsonb_build_object('valid', false, 'error', 'string too short');
        END IF;
        IF p_schema ? 'maxLength' AND length(v_text) > (p_schema ->> 'maxLength')::int THEN
            RETURN jsonb_build_object('valid', false, 'error', 'string too long');
        END IF;
    END IF;

    IF p_schema ? 'enum' AND NOT (p_schema -> 'enum') @> jsonb_build_array(p_data) THEN
        RETURN jsonb_build_object('valid', false, 'error', 'value not in enum');
    END IF;
    IF p_schema ? 'const' AND p_data <> (p_schema -> 'const') THEN
        RETURN jsonb_build_object('valid', false, 'error', 'value does not match const');
    END IF;

    IF jsonb_typeof(p_data) = 'object' THEN
        IF p_schema ? 'required' THEN
            FOR v_key IN SELECT jsonb_array_elements_text(p_schema -> 'required')
            LOOP
                IF NOT p_data ? v_key THEN
                    RETURN jsonb_build_object('valid', false, 'error', format('missing required field: %s', v_key));
                END IF;
            END LOOP;
        END IF;

        IF p_schema ? 'additionalProperties'
           AND (p_schema ->> 'additionalProperties') = 'false' THEN
            FOR v_key IN SELECT jsonb_object_keys(p_data)
            LOOP
                IF NOT (p_schema -> 'properties') ? v_key THEN
                    RETURN jsonb_build_object('valid', false, 'error', format('additional property: %s', v_key));
                END IF;
            END LOOP;
        END IF;

        IF p_schema ? 'properties' THEN
            FOR v_key, v_prop IN SELECT key, value FROM jsonb_each(p_schema -> 'properties')
            LOOP
                IF p_data ? v_key THEN
                    v_r := api.json_schema_validate(v_prop, p_data -> v_key);
                    IF NOT (v_r ->> 'valid')::boolean THEN
                        RETURN jsonb_build_object('valid', false, 'error', format('property %s: %s', v_key, v_r ->> 'error'));
                    END IF;
                END IF;
            END LOOP;
        END IF;
    END IF;

    RETURN jsonb_build_object('valid', true);
END;
$$;

CREATE OR REPLACE FUNCTION api.invoke(
    p_module text,
    p_action text,
    p_version integer,
    p_context jsonb,
    p_payload jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, autocheck, public
AS $$
DECLARE
    v_def record;
    v_scope text;
    v_validation jsonb;
    v_result jsonb;
    v_final jsonb;
    v_key text;
    v_scope_key text;
    v_payload_hash text;
    v_stored jsonb;
    v_stored_hash text;
    v_correlation uuid;
    v_msg text;
BEGIN
    IF p_version IS NOT NULL THEN
        SELECT * INTO v_def FROM autocheck.action_definitions d
        WHERE d.module = p_module AND d.action = p_action AND d.version = p_version AND d.enabled;
    ELSE
        SELECT * INTO v_def FROM autocheck.action_definitions d
        WHERE d.module = p_module AND d.action = p_action AND d.enabled AND d.is_default;
    END IF;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'action.not_found', 'message', 'action not found');
    END IF;

    IF coalesce(jsonb_array_length(v_def.manifest -> 'required_policy'), 0) > 0 THEN
        FOR v_scope IN SELECT jsonb_array_elements_text(v_def.manifest -> 'required_policy')
        LOOP
            IF NOT coalesce(p_context -> 'scopes', '[]'::jsonb) @> jsonb_build_array(v_scope) THEN
                RETURN jsonb_build_object('status', 'error', 'code', 'access.denied', 'message', 'missing required scope');
            END IF;
        END LOOP;
    END IF;

    v_key := p_context ->> 'idempotencyKey';
    IF (v_def.manifest ->> 'idempotency_mode') = 'required' AND v_key IS NULL THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.required', 'message', 'Idempotency-Key header is required');
    END IF;

    v_validation := api.json_schema_validate(v_def.manifest -> 'request_schema', p_payload);
    IF NOT (v_validation ->> 'valid')::boolean THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', v_validation ->> 'error');
    END IF;

    v_payload_hash := encode(digest(convert_to(p_payload::text, 'UTF8'), 'sha256'), 'hex');
    v_scope_key := coalesce(p_context ->> 'principal', 'anon') || '|' || p_module || '.' || p_action;

    IF v_key IS NOT NULL THEN
        SELECT o.result, o.payload_hash INTO v_stored, v_stored_hash
        FROM autocheck.operations o
        WHERE o.scope_key = v_scope_key AND o.idempotency_key = v_key;

        IF FOUND THEN
            IF v_stored_hash <> v_payload_hash THEN
                RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.conflict', 'message', 'same key with different payload');
            END IF;
            INSERT INTO autocheck.action_dispatches (module, action, version, request_id, correlation_id, payload_hash)
            VALUES (p_module, p_action, v_def.version, v_key, (v_stored -> 'meta' ->> 'correlationId')::uuid, v_payload_hash);
            RETURN v_stored;
        END IF;
    END IF;

    BEGIN
        v_correlation := (p_context ->> 'correlationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        v_correlation := gen_random_uuid();
    END;

    BEGIN
        EXECUTE format('SELECT %I.%I($1, $2)',
            v_def.manifest ->> 'target_schema',
            v_def.manifest ->> 'target_function')
        USING jsonb_set(p_context, '{correlationId}', to_jsonb(v_correlation::text)), p_payload
        INTO v_result;

        IF (v_result ->> 'status') = 'error' THEN
            RAISE EXCEPTION 'CTRL:%', v_result::text;
        END IF;

        IF NOT coalesce(v_def.manifest -> 'outcomes', '[]'::jsonb) @> jsonb_build_array(v_result ->> 'outcome') THEN
            RAISE EXCEPTION 'CTRL:%', jsonb_build_object(
                'status', 'error', 'code', 'action.contract_violation',
                'message', 'undeclared outcome')::text;
        END IF;

        v_validation := api.json_schema_validate(v_def.manifest -> 'response_schema', v_result -> 'result');
        IF NOT (v_validation ->> 'valid')::boolean THEN
            RAISE EXCEPTION 'CTRL:%', jsonb_build_object(
                'status', 'error', 'code', 'action.contract_violation',
                'message', v_validation ->> 'error')::text;
        END IF;

        v_final := jsonb_set(
            jsonb_set(v_result, '{meta}',
                coalesce(v_result -> 'meta', '{}'::jsonb) || jsonb_build_object(
                    'correlationId', v_correlation::text,
                    'actionVersion', v_def.version)),
            '{status}', '"ok"'::jsonb);

        INSERT INTO autocheck.action_dispatches (module, action, version, request_id, correlation_id, payload_hash)
        VALUES (p_module, p_action, v_def.version,
                coalesce(v_key, p_context ->> 'requestId', gen_random_uuid()::text),
                v_correlation, v_payload_hash);

        IF v_key IS NOT NULL THEN
            INSERT INTO autocheck.operations
                (operation_id, request_id, idempotency_key, scope_key, module, action, version,
                 status, payload, payload_hash, outcome, result)
            VALUES (gen_random_uuid(), v_key, v_key, v_scope_key, p_module, p_action, v_def.version,
                    v_result ->> 'outcome', p_payload, v_payload_hash, v_result ->> 'outcome', v_final);
        END IF;
    EXCEPTION WHEN OTHERS THEN
        GET STACKED DIAGNOSTICS v_msg = MESSAGE_TEXT;
        IF v_msg LIKE 'CTRL:%' THEN
            RETURN substring(v_msg, 6)::jsonb;
        END IF;
        RETURN jsonb_build_object('status', 'error', 'code', 'internal.error', 'message', v_msg);
    END;

    RETURN v_final;
END;
$$;

CREATE OR REPLACE FUNCTION api.payment_request(
    p_context jsonb,
    p_payload jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, autocheck, public
AS $$
DECLARE
    v_op_id uuid;
    v_req_id text;
    v_key text;
    v_scope_key text;
    v_kind text;
    v_amount numeric(18,2);
    v_currency text;
    v_stored record;
    v_payload_hash text;
    v_event_id uuid;
BEGIN
    v_req_id := p_context ->> 'requestId';
    v_key := p_context ->> 'idempotencyKey';
    v_scope_key := coalesce(p_context ->> 'principal', 'anon') || '|payment.request';

    IF v_key IS NULL THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.required');
    END IF;

    SELECT * INTO v_stored FROM autocheck.operations o
    WHERE o.scope_key = v_scope_key AND o.idempotency_key = v_key;

    IF FOUND THEN
        v_payload_hash := encode(digest(convert_to(p_payload::text, 'UTF8'), 'sha256'), 'hex');
        IF v_stored.payload_hash <> v_payload_hash THEN
            RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.conflict');
        END IF;
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'FOUND',
            'result', jsonb_build_object(
                'operationId', v_stored.operation_id,
                'requestId', v_req_id,
                'operationKind', v_stored.operation_kind,
                'amount', v_stored.amount::text,
                'currency', v_stored.currency,
                'status', v_stored.status
            )
        );
    END IF;

    v_kind := p_payload ->> 'operationKind';
    v_amount := (p_payload ->> 'amount')::numeric(18,2);
    v_currency := p_payload ->> 'currency';

    IF v_kind <> 'PAYMENT_EXECUTION' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'invalid operationKind');
    END IF;

    IF v_currency <> 'RUB' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'only RUB supported');
    END IF;

    IF v_amount <= 0 THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'amount must be positive');
    END IF;

    IF (p_payload ->> 'amount') !~ '^\d+\.\d{1,2}$' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'invalid amount format');
    END IF;

    v_op_id := gen_random_uuid();
    v_payload_hash := encode(digest(convert_to(p_payload::text, 'UTF8'), 'sha256'), 'hex');

    INSERT INTO autocheck.operations
        (operation_id, request_id, idempotency_key, scope_key, module, action, version,
         operation_kind, status, amount, currency, payload, payload_hash, outcome)
    VALUES (v_op_id, v_req_id, v_key, v_scope_key, 'payment', 'request', 1,
            v_kind, 'CREATED', v_amount, v_currency, p_payload, v_payload_hash, 'CREATED');

    v_event_id := gen_random_uuid();
    INSERT INTO autocheck.operation_events
        (event_id, operation_id, event_type, payload_hash)
    VALUES (v_event_id, v_op_id, 'OPERATION_CREATED', v_payload_hash);

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'CREATED',
        'result', jsonb_build_object(
            'operationId', v_op_id,
            'requestId', v_req_id,
            'operationKind', v_kind,
            'amount', v_amount::text,
            'currency', v_currency,
            'status', 'CREATED'
        )
    );
END;
$$;

CREATE OR REPLACE FUNCTION api.operation_get(
    p_context jsonb,
    p_payload jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, autocheck, public
AS $$
DECLARE
    v_op_id uuid;
    v_op record;
BEGIN
    v_op_id := (p_payload ->> 'operationId')::uuid;

    SELECT * INTO v_op FROM autocheck.operations WHERE operation_id = v_op_id;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'operation.not_found');
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', jsonb_build_object(
            'operationId', v_op.operation_id,
            'requestId', v_op.request_id,
            'operationKind', v_op.operation_kind,
            'amount', v_op.amount::text,
            'currency', v_op.currency,
            'status', v_op.status
        )
    );
END;
$$;

GRANT EXECUTE ON FUNCTION api.invoke TO course_runtime;
GRANT EXECUTE ON FUNCTION api.json_schema_validate TO course_runtime;
GRANT EXECUTE ON FUNCTION api.payment_request TO course_runtime;
GRANT EXECUTE ON FUNCTION api.operation_get TO course_runtime;