# Database-first action runtime

## Главный инвариант

> Клиент выбирает опубликованный action и передаёт payload, но не выбирает базу, schema, функцию, SQL, policy или предметный результат.

```text
POST /api/{module}/{action}
  -> gateway на :8080
  -> внутренний api по Compose DNS
  -> JWT и server-side context
  -> action manifest и request schema
  -> одна Npgsql transaction
  -> api.invoke(...)
  -> зарегистрированная PostgreSQL-функция
  -> outcome и response schema
  -> commit или rollback
```

## Технологии

C# .NET 10, ASP.NET Core, PostgreSQL 17, Docker Compose, Jwt "8.22.0" , Npgsql "10.0.3".

## Решение

### Архитектура

```text
[Client] → POST /api/{module}/{action}
         ↓
[Gateway :8080] → proxy → [Api :8080 (internal)]
         ↓                      ↓
    JWT validation        api.invoke(module, action, version, context, payload)
                                  ↓
                          [PostgreSQL :5432]
                          - autocheck.action_definitions
                          - api.json_schema_validate
                          - target function (dynamic)
                          - autocheck.action_dispatches
                          - autocheck.idempotency_claims
                          - autocheck.operations
```

- **Gateway**: HTTP proxy, JWT validation (HS256), whitelist routes, forward to Api via Compose DNS. Не содержит catalog, предметную логику и доступ к PostgreSQL.
- **Api**: Action runtime, JWT claims extraction, context building, единый generic route `POST /api/{module}/{action}`, вызов `api.invoke` в PostgreSQL внутри Npgsql-транзакции.
- **Cli**: Migration apply, action validate/publish/list/activate/disable.
- **PostgreSQL**: Schema `autocheck` (catalog, dispatches, idempotency_claims, operations, events), schema `api` (invoke, payment_request, operation_get functions).

Документация:
- C4 Container diagram: [`docs/c4-container.md`](docs/c4-container.md)
- ADR о trust boundary: [`docs/adr-001-trust-boundary.md`](docs/adr-001-trust-boundary.md)
- ADR о техническом и предметном результате: [`docs/adr-002-outcome-vs-result.md`](docs/adr-002-outcome-vs-result.md)

### Запуск

```bash
docker compose up -d --build
```

Prerequisites: Docker Desktop, Docker Compose.

Адрес: http://localhost:8080

Ожидаемый результат:

- `GET /health/live` → `200 {"status":"ok","service":"gateway"}`
- `GET /health/ready` → `200 {"status":"ok","service":"gateway","api":"up"}`
- `GET /openapi/default.json` → `200` OpenAPI 3.1.0 document

### Конфигурация

**Api:**

- `ConnectionStrings__Course`: `Host=postgres;Port=5432;Database=course;Username=course_api_login;Password=api_secret_change_me`
- `COURSE_JWT_ISSUER`: `moduledev-course` (можно переопределить через env)
- `COURSE_JWT_AUDIENCE`: `moduledev-api` (можно переопределить через env)
- `COURSE_JWT_SIGNING_KEY`: HS256 key ≥32 bytes (fallback: `moduledev-week1-rotated-key-do-not-use-in-production-2026-aug`)
- `MIGRATION_CONNECTION_STRING`: `Host=postgres;Port=5432;Database=course;Username=postgres;Password=postgres`

**Gateway:**

- `Api__BaseUrl`: `http://api:8080` (Compose DNS)

**Cli:**

- `ConnectionStrings__Course`: `Host=postgres;Port=5432;Database=course;Username=course_cli_login;Password=cli_secret_change_me`

Публичная проверка подменяет `COURSE_JWT_SIGNING_KEY` через Compose override.

### Миграции

- **Когда**: При старте Api (`DbMigrator.MigrateAsync`)
- **Какой сервис**: Api (`src/Api/DATA/DbMigrator.cs`)
- **Как**: Читает `Migrations/*.sql` из output directory, выполняет по порядку. Миграции выполняются отдельным connection string с повышенными привилегиями; runtime использует least-privilege роль.

Файлы:

- `001_schema.sql`: tables, roles, grants, idempotency_claims
- `002_api_functions.sql`: `api.json_schema_validate`, `api.invoke`
- `003_payment_v2.sql`: seed actions для payment и operation

### Проверка

```bash
./check.sh
```

Что делает:

1. `docker compose down -v --remove-orphans` (clean slate)
2. `docker compose up -d --build`
3. `cli migration apply /autocheck/input/migrations`
4. `cli action publish /autocheck/input/manifests/opencheck-probe-v1.action.json`
5. `cli action publish /autocheck/input/manifests/opencheck-probe-v2.action.json`
6. Запускает `autocheck/public_check.py` (black-box tests)

Ожидаемый результат: `status: passed` в `week-1-public-report.json`.

### Диагностика

Gateway logs:

```bash
docker compose logs gateway
```

Api logs:

```bash
docker compose logs api
```

Health:

```bash
curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
```

OpenAPI:

```bash
curl http://localhost:8080/openapi/default.json
```

PostgreSQL:

```bash
docker compose exec postgres psql -U postgres -d course
```

```sql
SELECT * FROM autocheck.action_definitions;
```

### Ограничения

- JSON Schema validator поддерживает subset Draft 2020-12: базовые типы (`object`, `string`, `number`, `integer`, `boolean`, `array`), `required`, `properties`, `additionalProperties`, `enum`, `const`, `minLength`, `maxLength`, `pattern`, `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`, `minItems`, `maxItems`, `items`.
- OpenAPI генерируется на лету из `action_definitions`, без кэширования.
- Concurrent idempotency защищается PostgreSQL `UNIQUE` constraint на `idempotency_claims`.
- Payment validation hardcoded: only RUB, amount format `^\d+\.\d{2}$`.
- No outbox/inbox — не входит в scope недели 1.
