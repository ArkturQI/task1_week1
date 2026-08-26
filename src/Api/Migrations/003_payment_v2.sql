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
    v_amount numeric(18,2);
    v_stored record;
    v_payload_hash text;
    v_k text;
BEGIN
    v_key := p_context ->> 'idempotencyKey';
    v_req_id := coalesce(v_key, p_context ->> 'requestId', gen_random_uuid()::text);
    v_scope_key := coalesce(p_context ->> 'principal', 'anon') || '|payment.request';

    IF v_key IS NULL THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.required', 'message', 'Idempotency-Key header is required');
    END IF;

    IF NOT coalesce(p_context -> 'scopes', '[]'::jsonb) @> jsonb_build_array('payment:write') THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'access.denied', 'message', 'missing scope payment:write');
    END IF;

    IF jsonb_typeof(p_payload) <> 'object' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'expected object');
    END IF;

    FOR v_k IN SELECT jsonb_object_keys(p_payload)
    LOOP
        IF v_k NOT IN ('operationKind', 'amount', 'currency') THEN
            RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'additional property: ' || v_k);
        END IF;
    END LOOP;

    IF NOT (p_payload ? 'operationKind' AND p_payload ? 'amount' AND p_payload ? 'currency') THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'missing required fields');
    END IF;

    IF jsonb_typeof(p_payload -> 'operationKind') <> 'string' OR (p_payload ->> 'operationKind') <> 'PAYMENT_EXECUTION' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'invalid operationKind');
    END IF;

    IF jsonb_typeof(p_payload -> 'currency') <> 'string' OR (p_payload ->> 'currency') <> 'RUB' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'only RUB supported');
    END IF;

    IF jsonb_typeof(p_payload -> 'amount') <> 'string' OR (p_payload ->> 'amount') !~ '^\d+\.\d{2}$' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'amount must be a string with two decimals');
    END IF;

    v_amount := (p_payload ->> 'amount')::numeric(18,2);
    IF v_amount <= 0 THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'amount must be positive');
    END IF;

    v_payload_hash := encode(digest(convert_to(p_payload::text, 'UTF8'), 'sha256'), 'hex');

    SELECT * INTO v_stored FROM autocheck.operations o
    WHERE o.scope_key = v_scope_key AND o.idempotency_key = v_key;

    IF FOUND THEN
        IF v_stored.payload_hash <> v_payload_hash THEN
            RETURN jsonb_build_object('status', 'error', 'code', 'idempotency.conflict', 'message', 'same key with different payload');
        END IF;
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'CREATED',
            'result', jsonb_build_object(
                'operationId', v_stored.operation_id,
                'requestId', v_stored.request_id,
                'operationKind', v_stored.operation_kind,
                'amount', v_stored.amount::text,
                'currency', v_stored.currency,
                'status', v_stored.status
            )
        );
    END IF;

    v_op_id := gen_random_uuid();

    BEGIN
        INSERT INTO autocheck.operations
            (operation_id, request_id, idempotency_key, scope_key, module, action, version,
             operation_kind, status, amount, currency, payload, payload_hash, outcome)
        VALUES (v_op_id, v_req_id, v_key, v_scope_key, 'payment', 'request', 1,
                'PAYMENT_EXECUTION', 'CREATED', v_amount, 'RUB', p_payload, v_payload_hash, 'CREATED');
    EXCEPTION WHEN unique_violation THEN
        SELECT * INTO v_stored FROM autocheck.operations o
        WHERE o.scope_key = v_scope_key AND o.idempotency_key = v_key;
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'CREATED',
            'result', jsonb_build_object(
                'operationId', v_stored.operation_id,
                'requestId', v_stored.request_id,
                'operationKind', v_stored.operation_kind,
                'amount', v_stored.amount::text,
                'currency', v_stored.currency,
                'status', v_stored.status
            )
        );
    END;

    INSERT INTO autocheck.operation_events (event_id, operation_id, event_type, payload_hash)
    VALUES (gen_random_uuid(), v_op_id, 'OPERATION_CREATED', v_payload_hash);

    INSERT INTO autocheck.action_dispatches (module, action, version, request_id, correlation_id, payload_hash)
    VALUES ('payment', 'request', 1, v_req_id, gen_random_uuid(), v_payload_hash);

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'CREATED',
        'result', jsonb_build_object(
            'operationId', v_op_id,
            'requestId', v_req_id,
            'operationKind', 'PAYMENT_EXECUTION',
            'amount', v_amount::text,
            'currency', 'RUB',
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
    IF NOT coalesce(p_context -> 'scopes', '[]'::jsonb) @> jsonb_build_array('payment:read') THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'access.denied', 'message', 'missing scope payment:read');
    END IF;

    IF jsonb_typeof(p_payload -> 'operationId') <> 'string' THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'operationId must be a string');
    END IF;

    BEGIN
        v_op_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'payload.invalid', 'message', 'operationId is not a uuid');
    END;

    SELECT * INTO v_op FROM autocheck.operations WHERE operation_id = v_op_id;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'operation.not_found', 'message', 'operation not found');
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