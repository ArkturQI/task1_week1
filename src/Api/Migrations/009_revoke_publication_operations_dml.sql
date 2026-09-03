-- Security hardening: publication/runtime logins must not mutate authoritative operation history.
-- API business changes happen only through SECURITY DEFINER functions owned by api_owner.

REVOKE api_owner FROM course_cli_login;

REVOKE INSERT, UPDATE, DELETE
ON autocheck.operations,
   autocheck.operation_events
FROM course_api, course_cli_login;

GRANT SELECT
ON autocheck.operations,
   autocheck.operation_events
TO course_cli_login;
