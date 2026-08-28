# Database-first action runtime

Database-first runtime для динамически публикуемых actions. Клиент выбирает только маршрут action и передаёт payload; `schema`, `target function`, SQL и policy определяются сервером из опубликованного catalog.

## Главный инвариант

> Клиент выбирает опубликованный action и передаёт payload, но не выбирает базу данных, schema, PostgreSQL-функцию, SQL, policy или предметный результат.

```text
Client
  │
  │ POST /api/{module}/{action}
  ▼
Gateway :8080
  │  JWT / route whitelist / proxy
  ▼
Api :8080 (internal)
  │  JWT claims + server-side context
  │  catalog + request schema
  │  Npgsql transaction
  ▼
PostgreSQL :5432
  ├─ autocheck.action_definitions
  ├─ autocheck.idempotency_claims
  ├─ autocheck.action_dispatches
  ├─ autocheck.operations
  ├─ autocheck.operation_events
  └─ api.* PostgreSQL functions
```

## Технологии

- C# / .NET 10
- ASP.NET Core
- PostgreSQL 17
- Docker Compose
- Npgsql 10.0.3
- System.IdentityModel.Tokens.Jwt 8.22.0
- xUnit 2.9.3

## Архитектура

### Gateway

Gateway — единственная публичная точка входа.

Он:

- принимает HTTP-запросы на `:8080`;
- валидирует JWT с HS256;
- проверяет whitelist маршрутов;
- проксирует разрешённые запросы во внутренний `Api` через Compose DNS;
- не содержит catalog, предметную логику и доступ к PostgreSQL.

### Api

Api — generic action runtime.

Он:

- принимает `POST /api/{module}/{action}`;
- извлекает JWT claims и формирует server-side context;
- проверяет action/version и request contract;
- выполняет `api.invoke(...)` через Npgsql;
- соблюдает transaction boundary и idempotency;
- возвращает технический `status/outcome` и предметный `result`.

### Cli

CLI используется для управления database-first runtime:

```text
migration apply

action validate

action publish
action list
action activate
action disable
```

### PostgreSQL

Используются схемы:

```text
autocheck
  ├─ contract_info
  ├─ schema_migrations
  ├─ action_definitions
  ├─ action_dispatches
  ├─ idempotency_claims
  ├─ operations
  └─ operation_events

api
  ├─ json_schema_validate
  ├─ invoke
  ├─ payment_request
  └─ operation_get
```

Для подключения используются отдельные роли:

```text
course_api_login          runtime API
course_cli_login          CLI / publication
course_migration_login    database migrations
course_runtime            read-only runtime role
course_api                publication role
api_owner                 NOLOGIN / NOSUPERUSER owner для SECURITY DEFINER
```

## Документация

- C4 Container diagram: [`docs/c4-container.md`](docs/c4-container.md)
- Trust boundary ADR: [`docs/adr-001-trust-boundary.md`](docs/adr-001-trust-boundary.md)
- Outcome vs result ADR: [`docs/adr-002-outcome-vs-result.md`](docs/adr-002-outcome-vs-result.md)
- Contract reference: [`docs/contract-reference.md`](docs/contract-reference.md)

## Требования

Для запуска нужны:

- Docker Desktop с Docker Compose;
- .NET 10 SDK — нужен для запуска собственных xUnit-тестов.

Проверять версии можно так:

```powershell
docker --version
docker compose version
dotnet --version
```

## Запуск

Из корня репозитория:

```powershell
docker compose up -d --build
```

Проверить состояние:

```powershell
docker compose ps
```

Ожидается:

- `postgres` — `healthy`;
- `db-bootstrap` — `Exited (0)`;
- `api` — `Up`;
- `gateway` — `Up`;
- `cli` — `Up`.

`db-bootstrap` завершает работу после создания login/group roles и выдачи bootstrap-привилегий. Это нормальное состояние; контейнер не обязан оставаться запущенным.

Публичная точка входа:

```text
http://localhost:8080
```

## Health и OpenAPI

### Live

```powershell
curl.exe -s http://localhost:8080/health/live
```

Ожидаемый ответ:

```json
{"status":"ok","service":"gateway"}
```

### Ready

```powershell
curl.exe -s http://localhost:8080/health/ready
```

Ожидаемый ответ:

```json
{"status":"ok","service":"gateway","api":"up"}
```

### OpenAPI

```powershell
curl.exe -s http://localhost:8080/openapi/default.json
```

Ожидается OpenAPI 3.1 document, построенный из актуального action catalog.

## Конфигурация

### Api

`compose.yaml` по умолчанию передаёт:

```text
ConnectionStrings__Course=Host=postgres;Port=5432;Database=course;Username=course_api_login;Password=api_secret_change_me
MIGRATION_CONNECTION_STRING=Host=postgres;Port=5432;Database=course;Username=course_migration_login;Password=migration_secret_change_me
COURSE_JWT_ISSUER=moduledev-course
COURSE_JWT_AUDIENCE=moduledev-api
COURSE_JWT_SIGNING_KEY=<HS256 key, минимум 32 bytes>
```

`COURSE_JWT_SIGNING_KEY` имеет compose fallback для локального запуска. Для проверки через внешний checker значение может быть переопределено Compose override.

### Gateway

```text
Api__BaseUrl=http://api:8080
```

Gateway обращается к Api через имя сервиса Compose `api`.

### Cli

```text
ConnectionStrings__Course=Host=postgres;Port=5432;Database=course;Username=course_cli_login;Password=cli_secret_change_me
```

## Миграции

### Startup migrations

При старте `Api` вызывается:

```text
src/Api/DATA/DbMigrator.cs
```

Мигратор:

1. подключается через `MIGRATION_CONNECTION_STRING`;
2. читает `Migrations/*.sql` из output directory;
3. сортирует файлы по имени;
4. выполняет каждый SQL-файл в отдельной transaction;
5. повторяет подключение при временной недоступности PostgreSQL.

Порядок миграций:

```text
001_schema.sql
002_api_functions.sql
003_payment_v2.sql
```

### CLI fixture migrations

Публичные fixtures можно применить отдельно:

```powershell
docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli migration apply /autocheck/input/migrations
```

## Публикация actions

Публикация выполняется через CLI.

Пример для fixture OpenCheck v1:

```powershell
docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v1.action.json
```

v2:

```powershell
docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v2.action.json
```

Список опубликованных actions:

```powershell
docker compose run --rm cli action list
```

## Собственные xUnit-тесты

Тестовый проект находится здесь:

```text
tests/Week1.Tests/Week1.Tests.csproj
```

Перед запуском тестов stack должен быть поднят и fixture actions должны быть доступны. Полный подготовительный сценарий:

```powershell
docker compose up -d --build

docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli migration apply /autocheck/input/migrations

docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v1.action.json
docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v2.action.json
```

Запуск всех тестов:

```powershell
dotnet test
```

или только проекта:

```powershell
dotnet test tests/Week1.Tests/Week1.Tests.csproj
```

Тесты по умолчанию используют:

```text
WEEK1_BASE_URL=http://localhost:8080
COURSE_JWT_ISSUER=moduledev-course
COURSE_JWT_AUDIENCE=moduledev-api
COURSE_JWT_SIGNING_KEY=<локальный compose key>
```

Переменные можно переопределить в PowerShell:

```powershell
$env:WEEK1_BASE_URL = "http://localhost:8080"
$env:COURSE_JWT_ISSUER = "moduledev-course"
$env:COURSE_JWT_AUDIENCE = "moduledev-api"
$env:COURSE_JWT_SIGNING_KEY = "your-test-key-at-least-32-bytes-long"

dotnet test tests/Week1.Tests/Week1.Tests.csproj
```

Текущий набор тестов проверяет в том числе:

- JWT validation;
- HTTP error mapping;
- live/ready health;
- malformed `X-Action-Version`;
- unknown action;
- required idempotency key;
- policy denial;
- explicit versions v1/v2;
- idempotent replay;
- idempotency payload conflict;
- concurrent requests with one idempotency key;
- payment request / operation flow.

## Полная публичная проверка

Основная black-box проверка запускается из корня репозитория:

```powershell
./check.sh
```

В Windows PowerShell это обычно выполняется через Git Bash, WSL или другую Bash-среду.

Скрипт `check.sh` передаёт управление:

```text
autocheck/check.sh
  -> autocheck/public_check.py
```

Checker использует fixtures из:

```text
autocheck/fixtures
```

и формирует:

```text
week-1-public-report.json
```

Для clean-slate сценария проверка сама выполняет:

```text
docker compose down -v --remove-orphans
docker compose up -d --build
```

затем применяет fixture migrations, публикует OpenCheck v1/v2 и запускает black-box проверки.

Ожидаемый итоговый статус:

```json
"status": "passed"
```

## Быстрый smoke test вручную

После `docker compose up -d --build` можно выполнить:

```powershell

docker compose ps

curl.exe -s http://localhost:8080/health/live
curl.exe -s http://localhost:8080/health/ready
curl.exe -s http://localhost:8080/openapi/default.json
```

Затем — fixture setup:

```powershell
docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli migration apply /autocheck/input/migrations

docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v1.action.json

docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v2.action.json
```

После этого:

```powershell
dotnet test
```

## Диагностика

### Все сервисы

```powershell
docker compose ps
```

### API logs

```powershell
docker compose logs api --tail 100
```

### Gateway logs

```powershell
docker compose logs gateway --tail 100
```

### Bootstrap logs

```powershell
docker compose logs db-bootstrap --no-log-prefix
```

Успешный bootstrap должен завершаться с кодом `0`.

### PostgreSQL logs

```powershell
docker compose logs postgres --tail 100
```

### PostgreSQL shell

```powershell
docker compose exec postgres psql -U postgres -d course
```

Примеры запросов:

```sql
SELECT *
FROM autocheck.action_definitions
ORDER BY module, action, version;
```

```sql
SELECT *
FROM autocheck.idempotency_claims
ORDER BY claimed_at DESC;
```

```sql
SELECT *
FROM autocheck.operations
ORDER BY created_at DESC;
```

### Полный clean slate

Если локальная база осталась в несовместимом состоянии после изменений ролей или схемы:

```powershell
docker compose down -v --remove-orphans
docker compose up -d --build
```

Команда `down -v` удаляет PostgreSQL volume, поэтому использовать её нужно только когда допустим полный сброс локальной базы.

## Безопасность и trust boundary

- Gateway — единственная опубликованная HTTP-точка.
- API использует отдельный login `course_api_login`, а не PostgreSQL superuser.
- CLI использует отдельный login `course_cli_login`.
- Миграции выполняются отдельным login `course_migration_login`.
- `api_owner` — `NOLOGIN NOSUPERUSER` owner для `SECURITY DEFINER` functions.
- Клиентские значения не используются для выбора произвольного SQL target.
- Server-side context формируется отдельно от пользовательского payload.
- Ошибки PostgreSQL не должны возвращаться клиенту как необработанный `MESSAGE_TEXT`.
- Idempotency claim создаётся до выполнения side-effecting target.
- `autocheck.idempotency_claims` защищён primary key по `(scope_key, idempotency_key)`.

## Ограничения текущей реализации

- JSON Schema validator реализует поддерживаемый subset Draft 2020-12, а не полный стандарт.
- OpenAPI генерируется на лету из `action_definitions`.
- Payment validation ограничен текущим контрактом: RUB и формат суммы с двумя знаками после десятичной точки.
- Outbox/inbox pattern не реализован и не входит в scope Week 1.
- Собственные xUnit-тесты покрывают ключевые runtime-сценарии, но не заменяют полную black-box проверку `autocheck/public_check.py`.

## Быстрый путь от чистого clone до проверки

```powershell
docker compose up -d --build

docker compose ps

docker compose logs db-bootstrap --no-log-prefix

docker compose logs api --tail 30

docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli migration apply /autocheck/input/migrations
docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v1.action.json
docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v2.action.json
dotnet test tests/Week1.Tests/Week1.Tests.csproj
```

Для официального black-box прогона:

```bash
./check.sh
```
