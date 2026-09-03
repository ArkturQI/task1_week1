# ADR-001: Trust Boundary

## Status
Accepted

## Context
Клиент не должен иметь возможности подменить context, policy, target или результат action. JWT claims (principal, consumer, scopes) должны быть достоверными и проверенными.

## Decision
Trust boundary проходит на двух уровнях ответственности:

1. **Gateway level**: Gateway является публичной HTTP-точкой входа, ограничивает маршруты и проксирует `Authorization` в API. Gateway не выполняет самостоятельную authoritative JWT-криптографическую валидацию; токен рассматривается как недоверенный вход и передаётся в API без извлечения доверенных claims.
2. **Api level**: API выполняет authoritative JWT валидацию (issuer, audience, expiration, signature и типы обязательных claims), строит context server-side, а `correlationId`, `requestId` и deadline формирует runtime. Payload не может содержать одноимённые с context поля.

PostgreSQL-функции (`api.invoke`, `api.payment_request`, `api.operation_get`) повторно проверяют `required_policy` по доверенному context.

## Consequences
- Gateway не является источником доверенных JWT claims и не извлекает их до валидации в API;
- principal, consumer, scopes невозможно подменить через payload;
- target_schema и target_function берутся только из catalog;
- policy проверяется на HTTP-границе API и повторно в `api.invoke`;
- ошибки не раскрывают SQL, connection string, stack trace и внутренние targets.
