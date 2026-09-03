-- Seed: payment.request action (writes to autocheck.operations via api.payment_request)
INSERT INTO autocheck.action_definitions (module, action, version, http_method, target_schema, target_function, outcomes, manifest, manifest_hash, enabled, is_default)
SELECT 'payment', 'request', 1, 'POST', 'api', 'payment_request', m.manifest -> 'outcomes', m.manifest,
       encode(digest(convert_to(m.manifest::text, 'UTF8'), 'sha256'), 'hex'), true, true
FROM (SELECT $${
  "contract_version": "course-1",
  "module": "payment",
  "action": "request",
  "version": 1,
  "http_method": "POST",
  "target_schema": "api",
  "target_function": "payment_request",
  "request_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["operationKind", "amount", "currency"],
    "properties": {
      "operationKind": {"enum": ["PAYMENT_EXECUTION", "PAYMENT_APPROVAL"]},
      "amount": {"type": "string", "minLength": 1, "maxLength": 20},
      "currency": {"const": "RUB"}
    }
  },
  "response_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "requestId", "operationKind", "amount", "currency", "status"],
    "properties": {
      "operationId": {"type": "string"},
      "requestId": {"type": "string"},
      "operationKind": {"type": "string"},
      "amount": {"type": "string"},
      "currency": {"type": "string"},
      "status": {"type": "string"}
    }
  },
  "outcomes": ["CREATED"],
  "required_policy": ["payment:write"],
  "idempotency_mode": "required",
  "idempotency_scope": "principal_action",
  "timeout_ms": 5000,
  "enabled": true,
  "is_default": true
}$$::jsonb AS manifest) m
ON CONFLICT (module, action, version) DO NOTHING;

-- Seed: operation.get action (reads from autocheck.operations via api.operation_get)
INSERT INTO autocheck.action_definitions (module, action, version, http_method, target_schema, target_function, outcomes, manifest, manifest_hash, enabled, is_default)
SELECT 'operation', 'get', 1, 'POST', 'api', 'operation_get', m.manifest -> 'outcomes', m.manifest,
       encode(digest(convert_to(m.manifest::text, 'UTF8'), 'sha256'), 'hex'), true, true
FROM (SELECT $${
  "contract_version": "course-1",
  "module": "operation",
  "action": "get",
  "version": 1,
  "http_method": "POST",
  "target_schema": "api",
  "target_function": "operation_get",
  "request_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": {
      "operationId": {"type": "string"}
    }
  },
  "response_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "requestId", "operationKind", "amount", "currency", "status"],
    "properties": {
      "operationId": {"type": "string"},
      "requestId": {"type": "string"},
      "operationKind": {"type": "string"},
      "amount": {"type": "string"},
      "currency": {"type": "string"},
      "status": {"type": "string"}
    }
  },
  "outcomes": ["FOUND"],
  "required_policy": ["payment:read"],
  "idempotency_mode": "optional",
  "idempotency_scope": "principal_action",
  "timeout_ms": 5000,
  "enabled": true,
  "is_default": true
}$$::jsonb AS manifest) m
ON CONFLICT (module, action, version) DO NOTHING;