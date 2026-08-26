# ADR-002: Technical vs Domain Result

## Status
Accepted

## Context
Runtime должен различать технические ошибки, предметные ошибки и нарушения контракта. Любой неуспех после вызова target-функции откатывает весь предметный эффект.

## Decision
Три категории результата:

1. **Technical error** (до вызова target): auth.invalid (401), access.denied (403), idempotency.required (400), idempotency.conflict (409), payload.invalid (422), action.not_found (404), dependency.unavailable (503).
2. **Domain error** (target вернул status=error): полный rollback, клиент получает контролируемый error envelope с кодом из target.
3. **Contract violation** (необъявленный outcome или result вне response schema): HTTP 500, code action.contract_violation, полный rollback.

Commit только при status=ok и объявленном outcome. Идемпотентный result сохраняется только для успешных envelope.

## Consequences
- canary и предметные таблицы не изменяются при ошибках;
- клиент получает точный error code без деталей реализации;
- конкурентная идемпотентность защищается PostgreSQL UNIQUE, а не process-local lock.