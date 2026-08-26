ALTER TABLE IF EXISTS autocheck.contract_info RENAME TO _contract_info_tbl;
ALTER TABLE IF EXISTS autocheck.action_definitions RENAME TO _action_definitions_tbl;
ALTER TABLE IF EXISTS autocheck.action_dispatches RENAME TO _action_dispatches_tbl;
ALTER TABLE IF EXISTS autocheck.operations RENAME TO _operations_tbl;
ALTER TABLE IF EXISTS autocheck.operation_events RENAME TO _operation_events_tbl;

CREATE OR REPLACE VIEW autocheck.contract_info AS
SELECT contract_version, generated_at FROM autocheck._contract_info_tbl;

CREATE OR REPLACE VIEW autocheck.action_definitions AS
SELECT module, action, version, http_method, target_schema, target_function, outcomes, enabled, is_default, manifest, manifest_hash
FROM autocheck._action_definitions_tbl;

CREATE OR REPLACE VIEW autocheck.action_dispatches AS
SELECT correlation_id, request_id, module, action, version, principal, payload_hash, status, outcome, occurred_at 
FROM autocheck._action_dispatches_tbl;

CREATE OR REPLACE VIEW autocheck.operations AS
SELECT operation_id, request_id, operation_kind, amount, currency, status, process_id, created_at, updated_at 
FROM autocheck._operations_tbl;

CREATE OR REPLACE VIEW autocheck.operation_events AS
SELECT event_id, operation_id, event_type, payload_hash, occurred_at 
FROM autocheck._operation_events_tbl;

REVOKE ALL ON autocheck.contract_info, autocheck.action_definitions, autocheck.action_dispatches, autocheck.operations, autocheck.operation_events FROM course_runtime;
GRANT SELECT ON autocheck.contract_info, autocheck.action_definitions, autocheck.action_dispatches, autocheck.operations, autocheck.operation_events TO course_runtime;