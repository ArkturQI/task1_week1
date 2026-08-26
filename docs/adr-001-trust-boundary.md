# ADR-001: Trust Boundary

## Status
Accepted

## Context
Клиент не должен иметь возможности подменить context, policy, target или результат action. JWT claims (principal, consumer, scopes) должны быть достоверными и проверенными.

## Decision
Trust boundary проходит на двух уровнях:

1. **Gateway level**: JWT валидация HS256 (issuer, audience, expiration, signature). Claims извлекаются только из проверенного JWT.
2. **Api level**: context строится server-side. correlationId, requestId и deadline формирует runtime. Payload не может содержать одноимённые с context поля.

PostgreSQL-функции (api.invoke, api.payment_request, api.operation_get) повторно проверяют required_policy по доверенному context.

## Consequences
- principal, consumer, scopes невозможно подменить через payload;
- target_schema и target_function берутся только из catalog;
- policy проверяется на HTTP-границе и повторно в api.invoke;
- ошибки не раскрывают SQL, connection string, stack trace и внутренние targets.