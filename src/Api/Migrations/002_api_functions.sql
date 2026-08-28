CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- =====================================================================
-- JSON Schema validator (расширенный subset Draft 2020-12)
-- =====================================================================
CREATE OR REPLACE FUNCTION api.json_schema_validate(p_schema jsonb, p_data jsonb)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE v_type text; v_key text; v_prop jsonb; v_r jsonb; v_text text; v_num numeric; v_item jsonb; v_i int;
BEGIN
    IF p_schema IS NULL OR p_data IS NULL THEN RETURN jsonb_build_object('valid', false, 'error', 'schema or data is null'); END IF;

    -- enum / const применимы к любому типу
    IF p_schema ? 'enum' AND NOT (p_schema -> 'enum') @> jsonb_build_array(p_data) THEN RETURN jsonb_build_object('valid', false, 'error', 'value not in enum'); END IF;
    IF p_schema ? 'const' AND p_data <> (p_schema -> 'const') THEN RETURN jsonb_build_object('valid', false, 'error', 'value does not match const'); END IF;

    v_type := p_schema ->> 'type';
    IF v_type = 'object' AND jsonb_typeof(p_data) <> 'object' THEN RETURN jsonb_build_object('valid', false, 'error', 'expected object'); END IF;
    IF v_type = 'string' AND jsonb_typeof(p_data) <> 'string' THEN RETURN jsonb_build_object('valid', false, 'error', 'expected string'); END IF;
    IF v_type = 'boolean' AND jsonb_typeof(p_data) <> 'boolean' THEN RETURN jsonb_build_object('valid', false, 'error', 'expected boolean'); END IF;
    IF v_type IN ('number', 'integer') AND jsonb_typeof(p_data) NOT IN ('number', 'integer') THEN RETURN jsonb_build_object('valid', false, 'error', 'expected number'); END IF;
    IF v_type = 'array' AND jsonb_typeof(p_data) <> 'array' THEN RETURN jsonb_build_object('valid', false, 'error', 'expected array'); END IF;
    IF v_type = 'integer' AND jsonb_typeof(p_data) IN ('number') AND (p_data #>> '{}') ~ '\.' THEN RETURN jsonb_build_object('valid', false, 'error', 'expected integer'); END IF;

    IF jsonb_typeof(p_data) = 'string' THEN
        v_text := p_data #>> '{}';
        IF p_schema ? 'minLength' AND length(v_text) < (p_schema ->> 'minLength')::int THEN RETURN jsonb_build_object('valid', false, 'error', 'string too short'); END IF;
        IF p_schema ? 'maxLength' AND length(v_text) > (p_schema ->> 'maxLength')::int THEN RETURN jsonb_build_object('valid', false, 'error', 'string too long'); END IF;
        IF p_schema ? 'pattern' AND NOT (v_text ~ (p_schema ->> 'pattern')) THEN RETURN jsonb_build_object('valid', false, 'error', 'string does not match pattern'); END IF;
    END IF;

    IF jsonb_typeof(p_data) IN ('number', 'integer') THEN
        v_num := (p_data #>> '{}')::numeric;
        IF p_schema ? 'minimum' AND v_num < (p_schema ->> 'minimum')::numeric THEN RETURN jsonb_build_object('valid', false, 'error', 'number below minimum'); END IF;
        IF p_schema ? 'maximum' AND v_num > (p_schema ->> 'maximum')::numeric THEN RETURN jsonb_build_object('valid', false, 'error', 'number above maximum'); END IF;
        IF p_schema ? 'exclusiveMinimum' AND v_num <= (p_schema ->> 'exclusiveMinimum')::numeric THEN RETURN jsonb_build_object('valid', false, 'error', 'number below exclusiveMinimum'); END IF;
        IF p_schema ? 'exclusiveMaximum' AND v_num >= (p_schema ->> 'exclusiveMaximum')::numeric THEN RETURN jsonb_build_object('valid', false, 'error', 'number above exclusiveMaximum'); END IF;
        IF p_schema ? 'multipleOf' AND mod(v_num, (p_schema ->> 'multipleOf')::numeric) <> 0 THEN RETURN jsonb_build_object('valid', false, 'error', 'number not multipleOf'); END IF;
    END IF;

    IF jsonb_typeof(p_data) = 'array' THEN
        IF p_schema ? 'minItems' AND jsonb_array_length(p_data) < (p_schema ->> 'minItems')::int THEN RETURN jsonb_build_object('valid', false, 'error', 'too few items'); END IF;
        IF p_schema ? 'maxItems' AND jsonb_array_length(p_data) > (p_schema ->> 'maxItems')::int THEN RETURN jsonb_build_object('valid', false, 'error', 'too many items'); END IF;
        IF p_schema ? 'items' THEN
            v_i := 0;
            FOR v_item IN SELECT jsonb_array_elements(p_data) LOOP
                v_r := api.json_schema_validate(p_schema -> 'items', v_item);
                IF NOT (v_r ->> 'valid')::boolean THEN RETURN jsonb_build_object('valid', false, 'error', format('item %s: %s', v_i, v_r ->> 'error')); END IF;
                v_i := v_i + 1;
            END LOOP;
        END IF;
    END IF;

    IF jsonb_typeof(p_data) = 'object' THEN
        IF p_schema ? 'required' THEN
            FOR v_key IN SELECT jsonb_array_elements_text(p_schema -> 'required') LOOP
                IF NOT p_data ? v_key THEN RETURN jsonb_build_object('valid', false, 'error', format('missing required field: %s', v_key)); END IF;
            END LOOP;
        END IF;
        IF p_schema ? 'additionalProperties' AND (p_schema ->> 'additionalProperties') = 'false' THEN
            FOR v_key IN SELECT jsonb_object_keys(p_data) LOOP
                IF NOT (p_schema -> 'properties') ? v_key THEN RETURN jsonb_build_object('valid', false, 'error', format('additional property: %s', v_key)); END IF;
            END LOOP;
        END IF;
        IF p_schema ? 'properties' THEN
            FOR v_key, v_prop IN SELECT key, value FROM jsonb_each(p_schema -> 'properties') LOOP
                IF p_data ? v_key THEN
                    v_r := api.json_schema_validate(v_prop, p_data -> v_key);
                    IF NOT (v_r ->> 'valid')::boolean THEN RETURN jsonb_build_object('valid', false, 'error', format('property %s: %s', v_key, v_r ->> 'error')); END IF;
                END IF;
            END LOOP;
        END IF;
    END IF;

    RETURN jsonb_build_object('valid', true);
END; $$;
ALTER FUNCTION api.json_schema_validate OWNER TO api_owner;

-- =====================================================================
-- Generic action dispatcher.
-- SECURITY DEFINER от api_owner (NOLOGIN NOSUPERUSER), НЕ от superuser.
-- Idempotency key claim-ится АТОМАРНО до выполнения target.
-- =====================================================================
CREATE OR REPLACE FUNCTION api.invoke(p_module text, p_action text, p_version integer, p_context jsonb, p_payload jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, autocheck, public AS $$
DECLARE
    v_def record; v_scope text; v_validation jsonb; v_result jsonb; v_final jsonb;
    v_key text; v_scope_key text; v_payload_hash text; v_stored jsonb; v_stored_hash text;
    v_correlation uuid; v_msg text; v_timeout_ms int; v_attempts int := 0; v_max_attempts int;
BEGIN
    -- Разрешаем version ровно один раз внутри транзакции
    IF p_version IS NOT NULL THEN
        SELECT * INTO v_def FROM autocheck.action_definitions d WHERE d.module = p_module AND d.action = p_action AND d.version = p_version AND d.enabled;
    ELSE
        SELECT * INTO v_def FROM autocheck.action_definitions d WHERE d.module = p_module AND d.action = p_action AND d.enabled AND d.is_default;
    END IF;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'action.not_found', 'message', 'action not found',
            'meta', jsonb_build_object('correlationId', coalesce(p_context ->> 'correlationId', gen_random_uuid()::text), 'actionVersion', p_version));
    END IF;

    v_timeout_ms := coalesce((v_def.manifest ->> 'timeout_ms')::int, 10000);
    BEGIN EXECUTE format('SET LOCAL statement_timeout = %L', v_timeout_ms); EXCEPTION WHEN OTHERS THEN NULL; END;

    IF coalesce(jsonb_array_length(v_def.manifest -> 'required_policy'), 0) > 0 THEN
        FOR v_scope IN SELECT jsonb_array_elements_text(v_def.manifest -> 'required_policy') LOOP
            IF NOT coalesce(p_context -> 'scopes', '[]'::jsonb) @> jsonb_build_array(v_scope) THEN
                RETURN jsonb_build_object('status', 'error', 'code', 'access.denied', 'message', 'missing required scope',
                    'meta', jsonb_build_object('correlationId', coalesce(p_context ->> 'correlationId', gen_random_uuid()::text), 'actionVersion', v_def.version));
            END IF;
        END LOOP;
    END IF;

    v_key := p_context ->> 'idempotencyKey';
    IF (v_def.manifest ->> 'idempotency_mode') = 'required' AND v_key IS NULL THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.required', 'message', 'Idempotency-Key header is required',
            'meta', jsonb_build_object('correlationId', coalesce(p_context ->> 'correlationId', gen_random_uuid()::text), 'actionVersion', v_def.version));
    END IF;

    v_validation := api.json_schema_validate(v_def.manifest -> 'request_schema', p_payload);
    IF NOT (v_validation ->> 'valid')::boolean THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', v_validation ->> 'error',
            'meta', jsonb_build_object('correlationId', coalesce(p_context ->> 'correlationId', gen_random_uuid()::text), 'actionVersion', v_def.version));
    END IF;

    v_payload_hash := encode(digest(convert_to(p_payload::text, 'UTF8'), 'sha256'), 'hex');
    CASE coalesce(v_def.manifest ->> 'idempotency_scope', 'principal_action')
        WHEN 'consumer_action' THEN v_scope_key := coalesce(p_context ->> 'consumer', 'anon') || '|' || p_module || '.' || p_action;
        WHEN 'global_action'   THEN v_scope_key := 'global|' || p_module || '.' || p_action;
        ELSE                        v_scope_key := coalesce(p_context ->> 'principal', 'anon') || '|' || p_module || '.' || p_action;
    END CASE;

    BEGIN v_correlation := (p_context ->> 'correlationId')::uuid; EXCEPTION WHEN OTHERS THEN v_correlation := gen_random_uuid(); END;

    -- =================================================================
    -- ATOMIC CLAIM: только владелец новой записи выполняет target.
    -- =================================================================
    IF v_key IS NOT NULL THEN
        v_max_attempts := GREATEST(1, v_timeout_ms / 50);
        LOOP
            INSERT INTO autocheck.idempotency_claims (scope_key, idempotency_key, payload_hash, status)
            VALUES (v_scope_key, v_key, v_payload_hash, 'PENDING')
            ON CONFLICT (scope_key, idempotency_key) DO NOTHING;

            IF FOUND THEN EXIT; END IF; -- мы владелец

            SELECT result, payload_hash INTO v_stored, v_stored_hash
            FROM autocheck.idempotency_claims
            WHERE scope_key = v_scope_key AND idempotency_key = v_key;

            IF NOT FOUND THEN
                CONTINUE; -- конкурент откатился, пробуем claim заново
            END IF;

            IF v_stored_hash <> v_payload_hash THEN
                RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.conflict', 'message', 'same key with different payload',
                    'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
            END IF;

            IF v_stored IS NOT NULL THEN
                -- Идемпотентный возврат: новый предметный эффект НЕ создаётся
                INSERT INTO autocheck.action_dispatches (module, action, version, request_id, correlation_id, principal, payload_hash, status, outcome, occurred_at)
                VALUES (p_module, p_action, v_def.version, v_key, coalesce((v_stored -> 'meta' ->> 'correlationId')::uuid, v_correlation),
                        coalesce(p_context ->> 'principal', ''), v_payload_hash, 'OK', v_stored ->> 'outcome', clock_timestamp());
                RETURN v_stored;
            END IF;

            -- Владелец ещё выполняет target: ждём завершения
            v_attempts := v_attempts + 1;
            IF v_attempts > v_max_attempts THEN
                RETURN jsonb_build_object('status', 'error', 'code', 'internal.error', 'message', 'idempotent request is still in progress', 'retryable', true,
                    'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
            END IF;
            PERFORM pg_sleep(0.05);
        END LOOP;
    END IF;

    BEGIN
        EXECUTE format('SELECT %I.%I($1, $2)', v_def.target_schema, v_def.target_function)
        USING jsonb_set(p_context, '{correlationId}', to_jsonb(v_correlation::text)), p_payload INTO v_result;

        IF (v_result ->> 'status') = 'error' THEN RAISE EXCEPTION 'CTRL:%', v_result::text; END IF;
        IF NOT coalesce(v_def.outcomes, '[]'::jsonb) @> jsonb_build_array(v_result ->> 'outcome') THEN
            RAISE EXCEPTION 'CTRL:%', jsonb_build_object('status', 'error', 'code', 'action.contract_violation', 'message', 'undeclared outcome')::text;
        END IF;

        v_validation := api.json_schema_validate(v_def.manifest -> 'response_schema', v_result -> 'result');
        IF NOT (v_validation ->> 'valid')::boolean THEN
            RAISE EXCEPTION 'CTRL:%', jsonb_build_object('status', 'error', 'code', 'action.contract_violation', 'message', v_validation ->> 'error')::text;
        END IF;

        v_final := jsonb_set(jsonb_set(v_result, '{meta}', coalesce(v_result -> 'meta', '{}'::jsonb) || jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version)), '{status}', '"ok"'::jsonb);

        INSERT INTO autocheck.action_dispatches (module, action, version, request_id, correlation_id, principal, payload_hash, status, outcome, occurred_at)
        VALUES (p_module, p_action, v_def.version, coalesce(v_key, p_context ->> 'requestId', gen_random_uuid()::text), v_correlation,
                coalesce(p_context ->> 'principal', ''), v_payload_hash, 'OK', v_result ->> 'outcome', clock_timestamp());

        -- Завершаем claim в той же транзакции: target + result + claim атомарны
        IF v_key IS NOT NULL THEN
            UPDATE autocheck.idempotency_claims SET status = 'COMPLETED', result = v_final, completed_at = clock_timestamp()
            WHERE scope_key = v_scope_key AND idempotency_key = v_key;
        END IF;
    EXCEPTION WHEN OTHERS THEN
        GET STACKED DIAGNOSTICS v_msg = MESSAGE_TEXT;
        IF v_msg LIKE 'CTRL:%' THEN RETURN substring(v_msg, 6)::jsonb; END IF;
        -- Полная диагностика — только в серверный лог, клиенту фиксированное сообщение
        RAISE LOG 'api.invoke failed correlation=% module=% action=% error=%', v_correlation::text, p_module, p_action, v_msg;
        RETURN jsonb_build_object('status', 'error', 'code', 'internal.error', 'message', 'internal error', 'retryable', true,
            'meta', jsonb_build_object('correlationId', v_correlation::text, 'actionVersion', v_def.version));
    END;
    RETURN v_final;
END; $$;
ALTER FUNCTION api.invoke OWNER TO api_owner;

-- =====================================================================
-- Payment target: сам создаёт свою operation и initial event.
-- Generic idempotency больше не трогает предметные таблицы.
-- =====================================================================
CREATE OR REPLACE FUNCTION api.payment_request(p_context jsonb, p_payload jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, autocheck, public AS $$
DECLARE v_op_id uuid; v_req_id text; v_kind text; v_amount numeric(18,2); v_currency text; v_payload_hash text; v_final jsonb;
BEGIN
    v_req_id := coalesce(p_context ->> 'requestId', gen_random_uuid()::text);
    IF NOT coalesce(p_context -> 'scopes', '[]'::jsonb) @> jsonb_build_array('payment:write') THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'access.denied', 'message', 'missing scope payment:write'); END IF;
    IF jsonb_typeof(p_payload) <> 'object' THEN RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'expected object'); END IF;
    IF p_payload ? 'target_schema' OR p_payload ? 'target_function' OR p_payload ? 'sql' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'additional property: reserved field'); END IF;
    IF NOT (p_payload ? 'operationKind' AND p_payload ? 'amount' AND p_payload ? 'currency') THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'missing required fields'); END IF;
    IF jsonb_typeof(p_payload -> 'operationKind') <> 'string' OR (p_payload ->> 'operationKind') NOT IN ('PAYMENT_EXECUTION', 'PAYMENT_APPROVAL') THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'invalid operationKind'); END IF;
    IF jsonb_typeof(p_payload -> 'currency') <> 'string' OR (p_payload ->> 'currency') <> 'RUB' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'only RUB supported'); END IF;
    IF jsonb_typeof(p_payload -> 'amount') <> 'string' OR (p_payload ->> 'amount') !~ '^\d+\.\d{2}$' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'amount must be a string with two decimals'); END IF;
    v_amount := (p_payload ->> 'amount')::numeric(18,2);
    IF v_amount <= 0 OR v_amount > 9999999999999999.99 THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'amount out of range'); END IF;
    v_kind := p_payload ->> 'operationKind'; v_currency := p_payload ->> 'currency';
    v_payload_hash := encode(digest(convert_to(p_payload::text, 'UTF8'), 'sha256'), 'hex');
    v_op_id := gen_random_uuid();

    v_final := jsonb_build_object('status', 'ok', 'outcome', 'CREATED', 'result', jsonb_build_object(
        'operationId', v_op_id, 'requestId', v_req_id, 'operationKind', v_kind, 'amount', v_amount::text, 'currency', v_currency, 'status', 'CREATED'));

    INSERT INTO autocheck.operations (operation_id, request_id, idempotency_key, scope_key, module, action, version, operation_kind, status, amount, currency, payload, payload_hash, outcome, result)
    VALUES (v_op_id, v_req_id, coalesce(p_context ->> 'idempotencyKey', ''), coalesce(p_context ->> 'principal', 'anon') || '|payment.request',
            'payment', 'request', 1, v_kind, 'CREATED', v_amount, v_currency, p_payload, v_payload_hash, 'CREATED', v_final);

    INSERT INTO autocheck.operation_events (event_id, operation_id, event_type, payload_hash, occurred_at)
    VALUES (gen_random_uuid(), v_op_id, 'OPERATION_CREATED', v_payload_hash, clock_timestamp());

    RETURN v_final;
END; $$;
ALTER FUNCTION api.payment_request OWNER TO api_owner;

CREATE OR REPLACE FUNCTION api.operation_get(p_context jsonb, p_payload jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, autocheck, public AS $$
DECLARE v_op_id uuid; v_op record;
BEGIN
    IF NOT coalesce(p_context -> 'scopes', '[]'::jsonb) @> jsonb_build_array('payment:read') THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'access.denied', 'message', 'missing scope payment:read'); END IF;
    IF jsonb_typeof(p_payload -> 'operationId') <> 'string' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'operationId must be a string'); END IF;
    BEGIN v_op_id := (p_payload ->> 'operationId')::uuid; EXCEPTION WHEN OTHERS THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'operationId is not a uuid'); END;
    SELECT * INTO v_op FROM autocheck.operations WHERE operation_id = v_op_id;
    IF NOT FOUND THEN RETURN jsonb_build_object('status', 'error', 'code', 'operation.not_found', 'message', 'operation not found'); END IF;
    RETURN jsonb_build_object('status', 'ok', 'outcome', 'FOUND', 'result', jsonb_build_object(
        'operationId', v_op.operation_id, 'requestId', v_op.request_id, 'operationKind', v_op.operation_kind,
        'amount', v_op.amount::text, 'currency', v_op.currency, 'status', v_op.status));
END; $$;
ALTER FUNCTION api.operation_get OWNER TO api_owner;

-- Права: runtime-роли только EXECUTE, никаких прямых таблиц
GRANT EXECUTE ON FUNCTION api.invoke TO course_api_login, course_runtime;
GRANT EXECUTE ON FUNCTION api.json_schema_validate TO course_api_login, course_runtime;
GRANT EXECUTE ON FUNCTION api.payment_request TO course_api_login, course_runtime;
GRANT EXECUTE ON FUNCTION api.operation_get TO course_api_login, course_runtime;