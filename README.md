# Зона 1. Шлюз

Открытое задание первой недели ModuleDev. В репозитории опубликованы условие, machine-readable contracts и открытая black-box проверка. Готового решения, внутренней шкалы и закрытых fixtures здесь нет.

С 22 по 26 августа 2026 года нужно построить первый слой учебной платформы: отдельный C# gateway и generic C# runtime, который публикует зарегистрированные PostgreSQL-функции как HTTP actions.

Проверка после сборки добавит новую функцию и manifest с неизвестными заранее именами. Новый action должен заработать без изменения и пересборки C# API.

## Срок и сдача

- начало работы: 22 августа 2026 года;
- дедлайн: 26 августа 2026 года, 23:59 по московскому времени;
- до дедлайна отправьте куратору URL приватного Git-репозитория, название ветки и полный SHA коммита;
- заранее предоставьте куратору доступ к репозиторию;
- проверяется указанный коммит; более поздние изменения учитываются только по отдельной договорённости с куратором.

Архив в чате, ссылка без доступа или сообщение без commit SHA не считаются сдачей.

## Результат недели

```text
POST /api/{module}/{action}
  -> gateway на :8080
  -> внутренний api по Compose DNS
  -> JWT и server-side context
  -> action manifest
  -> request schema
  -> одна Npgsql transaction
  -> api.invoke(...)
  -> PostgreSQL target function
  -> outcome и response schema
  -> commit или rollback
```

Главный инвариант:

> Клиент выбирает опубликованный action и передает payload, но не выбирает базу, схему, функцию, SQL, policy или предметный результат.

## Технологии

- C# и актуальный LTS .NET;
- ASP.NET Core;
- PostgreSQL;
- Docker Compose.

Можно выбирать библиотеки, ORM или чистый Npgsql, структуру solution, физическую модель таблиц и способ организации кода. AI-инструменты разрешены. Оценивается только воспроизводимый результат.

## Что остается на ваше усмотрение

- число C# projects и каталогов;
- ORM, query builder или чистый SQL;
- физические таблицы, индексы и имена внутренних объектов;
- кеширование action manifest;
- формат внутренних классов и интерфейсов;
- реализация idempotency storage;
- набор собственных unit и integration tests; на первой неделе он рекомендуется, но не входит в admission checks и не заменяет обязательную открытую проверку;
- библиотеки JSON Schema и OpenAPI.

Автопроверка не ищет конкретные классы и не требует Clean Architecture. Небольшая реализация с доказанными инвариантами предпочтительнее большого scaffold.

## Интерфейс запуска

В корне решения обязательны `compose.yaml` или `docker-compose.yml` и сервисы:

| Сервис | Обязательное свойство |
|---|---|
| `gateway` | Собирает и запускает C# ASP.NET Core gateway, единственный публикует host-порт `8080` |
| `api` | Собирает и запускает внутренний C# action runtime без опубликованных host-портов |
| `cli` | Запускает Course CLI; entrypoint принимает аргументы команд ниже |
| `postgres` | PostgreSQL с базой `course`, локальной maintenance-ролью `postgres` и утилитой `psql` |

Роль `postgres` используется проверкой только через `docker compose exec` внутри изолированного test profile и не публикуется по сети. Дополнительные сервисы допустимы. Данные PostgreSQL должны находиться в named volume и переживать удаление контейнеров `gateway` и `api`.

Чистый запуск:

```bash
docker compose up -d --build
```

После запуска не нужны ручные SQL-команды, публикация встроенных actions или настройка БД.

Внутренний `api` читает учебную JWT-конфигурацию из переменных:

| Переменная | Значение в открытом профиле |
|---|---|
| `COURSE_JWT_ISSUER` | `moduledev-course` |
| `COURSE_JWT_AUDIENCE` | `moduledev-api` |
| `COURSE_JWT_SIGNING_KEY` | HS256 key длиной не менее 32 байт |

Закрытая проверка подменяет signing key через Compose override. Реальный секрет в репозитории не нужен.

## Gateway

`gateway` — отдельный упрощённый edge-сервис. Он не содержит action catalog, предметную логику и доступ к PostgreSQL.

Обязательный контракт:

- внешний клиент обращается только к `http://localhost:8080`;
- `gateway` принимает по whitelist только маршруты actions, OpenAPI и health, описанные в этом задании;
- `gateway` передаёт метод, path, query, body и контрактные заголовки во внутренний `api` без изменения их смысла;
- `gateway` обращается к `api` по имени Compose-сервиса, а не через `localhost` или опубликованный host-порт;
- `api` самостоятельно проверяет JWT и не доверяет identity/scopes из произвольных входных заголовков;
- произвольный upstream, SQL target, имя БД, schema или function нельзя выбрать из запроса;
- ответ `api`, включая HTTP status и envelope, возвращается клиенту без подмены предметного результата;
- `GET /health/live` показывает, что процесс `gateway` жив;
- `GET /health/ready` возвращает 200 только когда внутренний `api` готов принимать запросы.

Можно использовать YARP, `HttpClient` или другой способ проксирования. Конкретная библиотека не оценивается.

В безопасной диагностике gateway допустимы route, upstream service name, HTTP status, duration и correlation metadata. JWT, credentials и полный payload в лог не попадают. Consul, CORS-трансформации, micro-cache, retries, rate limiting и очередь запросов в этой неделе не требуются.

## Course CLI

Публичная обертка может выглядеть так:

```bash
#!/usr/bin/env bash
exec docker compose run --rm -T cli "$@"
```

Проверка не исполняет `course.sh` из недоверенного репозитория. Она вызывает сервис `cli` напрямую и монтирует доверенные fixtures read-only.

Обязательные команды:

```text
course.sh migration apply <directory>
course.sh action validate <manifest>
course.sh action publish <manifest>
course.sh action list
course.sh action activate <module.action> --version <version>
course.sh action disable <module.action> --version <version> [--replacement-version <version>]
```

Команда принимает абсолютный или относительный путь, доступный внутри контейнера `cli`. Для автопроверки каталог монтируется как `/autocheck/input:ro`.

CLI пишет в stdout ровно один JSON-документ. Диагностика допускается только в stderr. При ошибке exit code ненулевой.

Успех:

```json
{
  "status": "ok",
  "result": {
    "resource": "action",
    "operation": "published",
    "key": "payment.request",
    "version": 1
  },
  "meta": {
    "contractVersion": "course-1"
  }
}
```

Ошибка:

```json
{
  "status": "error",
  "code": "manifest.conflict",
  "message": "published action version is immutable",
  "meta": {
    "contractVersion": "course-1"
  }
}
```

Правила:

- `action validate` не меняет данные;
- повторный `action publish` того же manifest безопасен;
- изменение опубликованной версии дает conflict;
- `action activate` атомарно включает выбранную версию, делает ее default и снимает default с прежней;
- `action disable` требует replacement, если route иначе останется с включенными версиями без default;
- `migration apply` выполняет `.sql` в лексикографическом порядке, по одной транзакции на файл;
- повтор migration с тем же checksum безопасен;
- изменение уже примененного файла дает conflict;
- API и будущий worker не используют migration credentials.

## Action manifest

Каноническая machine schema находится в `contracts/course-1/action-manifest.schema.json`.

Опубликованная версия неизменяема. `enabled` и `is_default` являются операционным состоянием и меняются только CLI-командами.

Для route с включенными версиями существует ровно одна default-версия. В обязательной части поддерживается только `POST`.

## Database-first выполнение

Единственная точка предметного выполнения:

```sql
api.invoke(
  p_module text,
  p_action text,
  p_version integer,
  p_context jsonb,
  p_payload jsonb
) returns jsonb
```

Target-функция имеет сигнатуру:

```sql
<target_schema>.<target_function>(
  p_context jsonb,
  p_payload jsonb
) returns jsonb
```

`api.invoke` обязан:

1. разрешить explicit или default version только из каталога;
2. отклонить неизвестный или выключенный action до target;
3. повторно проверить все scopes из `required_policy` по доверенному context;
4. проверить target и точную сигнатуру;
5. вызвать только зарегистрированную функцию с фиксированным `search_path`;
6. вернуть единый envelope.

HTTP executor валидирует request schema до предметного вызова. Затем он открывает Npgsql transaction, вызывает `api.invoke`, проверяет envelope, outcome и `result` по response schema и только после этого выполняет commit.

Если target изменил данные и вернул `status=error`, неизвестный outcome или несовместимый result, вся транзакция откатывается.

Runtime-роль имеет право выполнить `api.invoke`, но не получает прямой доступ к предметным таблицам и функциям. Владелец security-definer функций не имеет права входа.

## Доверенный context

C# формирует context после проверки JWT. Минимальная форма:

```json
{
  "principal": "candidate-client",
  "consumer": "web",
  "scopes": ["payment:write", "payment:read"],
  "correlationId": "1e534bdb-73a8-446a-a8f5-49c4959786c2",
  "requestId": "request-123",
  "deadline": "2026-08-28T12:00:02Z"
}
```

Правила:

- `principal`, `consumer` и `scopes` берутся только из проверенного JWT;
- `correlationId` генерируется runtime как UUID для каждого HTTP-запроса;
- `requestId` равен `Idempotency-Key`, если key передан;
- `deadline` вычисляется runtime из `timeout_ms` manifest;
- одноименные поля payload не меняют context;
- `api.invoke` считает policy выполненной, только если context содержит все scopes manifest.

Учебные principals:

| `sub` | `consumer` | `scope` |
|---|---|---|
| `candidate-client` | `web` | `payment:write payment:read workflow:read` |
| `workflow-worker` | `internal` | `workflow:execute payment:internal` |
| `reviewer` | `backoffice` | `workflow:manual payment:read` |
| `denied-client` | `test` | пусто |

JWT использует HS256, issuer и audience из конфигурации, а также claims `sub`, `consumer`, `scope`, `iat`, `exp`.

## HTTP API

```http
POST /api/{module}/{action}
Authorization: Bearer <token>
X-Action-Version: 1
Idempotency-Key: request-123
Content-Type: application/json
```

После `/api` всегда ровно два route-сегмента. Версия передается только в `X-Action-Version`. Без заголовка выбирается default version.

Успех всегда отвечает `200 OK`:

```json
{
  "status": "ok",
  "outcome": "CREATED",
  "result": {},
  "meta": {
    "correlationId": "1e534bdb-73a8-446a-a8f5-49c4959786c2",
    "actionVersion": 1
  }
}
```

Ошибка:

```json
{
  "status": "error",
  "code": "payload.invalid",
  "message": "payload does not match schema",
  "retryable": false,
  "details": {},
  "meta": {
    "correlationId": "1e534bdb-73a8-446a-a8f5-49c4959786c2",
    "actionVersion": 1
  }
}
```

| Ситуация | HTTP | `code` |
|---|---:|---|
| Неверный, отсутствующий или просроченный JWT | 401 | `auth.invalid` |
| Невалидный JSON, route или version header | 400 | `request.invalid` |
| Нет обязательного idempotency key | 400 | `idempotency.required` |
| Недостаточная policy | 403 | `access.denied` |
| Неизвестный или выключенный action/version | 404 | `action.not_found` |
| Тот же key с другим payload | 409 | `idempotency.conflict` |
| Payload не соответствует request schema | 422 | `payload.invalid` |
| Временная недоступность PostgreSQL | 503 | `dependency.unavailable` |
| Result или outcome нарушает manifest | 500 | `action.contract_violation` |
| Истек timeout | 504 | `action.timeout` |
| Необработанная ошибка | 500 | `internal.error` |

После успешной аутентификации `meta.correlationId` обязателен. Для ошибки разрешения action `meta.actionVersion` равен фактически выбранной версии или `null`.

Ответ 500 не раскрывает SQL, schema/function names, connection string и stack trace.

## Нормативная матрица исполнения

| Сценарий | HTTP/envelope | Предметный эффект | Проверяемое доказательство |
|---|---|---|---|
| Новый зарегистрированный action | `200`, объявленный `outcome`, schema-valid `result` | Commit target-изменения | Action доступен без изменения images gateway/API |
| Невалидный request payload | `422 payload.invalid` | Target не вызывается | Canary и предметные таблицы не изменены |
| Недостаточная policy | `403 access.denied` | Target не вызывается | Отказ подтверждён на HTTP-границе и в `api.invoke` |
| Повтор с тем же key и payload | Исходный успешный envelope | Второго эффекта нет | Та же operation и одно начальное событие |
| Тот же key с другим payload | `409 idempotency.conflict` | Нового эффекта нет | Авторитетное состояние не изменено |
| Target вернул `status=error` | Контролируемый error envelope | Полный rollback target-изменения | Canary отсутствует, dispatch содержит техническую ошибку |
| Неизвестный outcome или invalid result | `500 action.contract_violation` | Полный rollback target-изменения | Canary отсутствует, успешный idempotency result не сохранён |
| Неизвестная или выключенная версия | `404 action.not_found` | Target не вызывается | В dispatch нет успешного предметного вызова |
| Recreate gateway/API | После readiness возвращается исходный результат | PostgreSQL-состояние сохранено | `operation.get` возвращает ту же operation |

## Обязательные предметные actions

### `payment.request` version 1

Route: `POST /api/payment/request`.

Manifest properties:

| Поле | Значение |
|---|---|
| `required_policy` | `["payment:write"]` |
| `idempotency_mode` | `required` |
| `idempotency_scope` | `principal_action` |
| `outcomes` | `["CREATED"]` |

Payload:

```json
{
  "operationKind": "PAYMENT_EXECUTION",
  "amount": "1000.00",
  "currency": "RUB"
}
```

`operationKind` принимает `PAYMENT_EXECUTION` или `PAYMENT_APPROVAL`. `amount` является строкой от `0.01` до `9999999999999999.99`, без exponent и не более чем с двумя знаками после точки. Поддерживается только `RUB`. Неизвестные поля запрещены.

Успешный `result`:

```json
{
  "operationId": "8c26513d-8441-43ea-b064-3bca8c240052",
  "requestId": "request-123",
  "operationKind": "PAYMENT_EXECUTION",
  "amount": "1000.00",
  "currency": "RUB",
  "status": "CREATED"
}
```

Первый запрос атомарно создает operation и одно событие `OPERATION_CREATED`. Идентичный повтор возвращает ту же operation. Тот же key с другим payload возвращает `409 idempotency.conflict`. Конкурентные одинаковые запросы создают один предметный эффект за счет гарантии PostgreSQL, а не process-local lock.

Клиент не передает workflow name/version или финальный статус. На первой неделе process еще не создается.

### `operation.get` version 1

Route: `POST /api/operation/get`.

Manifest properties:

| Поле | Значение |
|---|---|
| `required_policy` | `["payment:read"]` |
| `idempotency_mode` | `none` |
| `idempotency_scope` | `none` |
| `outcomes` | `["FOUND"]` |

Payload:

```json
{
  "operationId": "8c26513d-8441-43ea-b064-3bca8c240052"
}
```

`result` имеет ту же форму operation, что `payment.request`. Неизвестный ID возвращает контролируемую предметную ошибку, а не 500.

Machine schemas находятся в `contracts/course-1`.

## OpenAPI

Обязательные endpoints:

- `GET /openapi/default.json` содержит только включенные default routes;
- `GET /openapi/actions/{module}/{action}/{version}.json` содержит одну точную версию action.

Документ строится из опубликованного manifest. Версии одного action не объединяются через `oneOf`. После `activate` или `disable` default document меняется без пересборки API.

## Health

- `GET /health/live` возвращает 200, если HTTP-процесс жив;
- `GET /health/ready` возвращает 200 только при доступном PostgreSQL и завершенной обязательной инициализации.

Проверка ждет readiness перед сценариями.

## Проверочные проекции

Физические таблицы остаются вашими. Для black-box проверки создайте read-only schema `autocheck` и views:

| View | Обязательные колонки |
|---|---|
| `contract_info` | `contract_version text`, `generated_at timestamptz` |
| `action_definitions` | `module text`, `action text`, `version integer`, `http_method text`, `target_schema text`, `target_function text`, `outcomes jsonb`, `enabled boolean`, `is_default boolean` |
| `action_dispatches` | `correlation_id uuid`, `request_id text`, `module text`, `action text`, `version integer`, `principal text`, `payload_hash text`, `status text`, `outcome text`, `occurred_at timestamptz` |
| `operations` | `operation_id uuid`, `request_id text`, `operation_kind text`, `amount numeric`, `currency text`, `status text`, `process_id uuid`, `created_at timestamptz`, `updated_at timestamptz` |
| `operation_events` | `event_id uuid`, `operation_id uuid`, `event_type text`, `payload_hash text`, `occurred_at timestamptz` |

Требования:

- `contract_info` содержит ровно одну строку с `contract_version = 'course-1'`;
- enum-подобные значения используют uppercase ASCII;
- `action_dispatches.status` принимает `OK` или `ERROR`;
- `operations.status` принимает `CREATED`, `PROCESSING`, `COMPLETED`, `REJECTED`;
- `payload_hash` является lowercase SHA-256 hex, полный payload не публикуется;
- `process_id` на первой неделе равен `null`;
- views не раскрывают JWT, signing key, connection string и полный payload.

### Неизменяемость и модель угроз PostgreSQL

Неизменяемость проверяется относительно прикладных ролей, а не superuser PostgreSQL:

- `course_runtime` может читать `autocheck` views, но не может выполнять через них `INSERT`, `UPDATE` или `DELETE`;
- identity и payload-поля операции (`operation_id`, `request_id`, `operation_kind`, `amount`, `currency`, `created_at`) не изменяются после создания;
- `operation_events` является insert-only history: прикладные runtime/publication roles не могут изменять или удалять события;
- изменение предметного состояния выполняют только зарегистрированные функции через доверенную транзакционную границу;
- object owner, используемый `SECURITY DEFINER`, имеет `NOLOGIN`; административный superuser не входит в модель угроз задания.

Открытая и скрытая проверки выполняют отрицательные mutation probes после `SET ROLE course_runtime`. Физическая модель таблиц остаётся свободной, поэтому стабильной границей проверки служат `autocheck` views и права ролей.

Автопроверка выполняет `SELECT` внутри контейнера `postgres`. Она не зависит от физических таблиц и ORM.

## Что реализовать

- отдельный C# ASP.NET Core service `gateway` как единственную внешнюю точку входа;
- migrations PostgreSQL и роли publication/runtime;
- immutable action catalog;
- `api.invoke(...)`;
- generic C# endpoint;
- общий action executor с request/response schema validation;
- JWT и server-side context;
- CLI migration/action;
- actions `payment.request` и `operation.get` как PostgreSQL-функции;
- идемпотентность и конкурентную уникальность;
- единый error contract;
- manifest-driven OpenAPI;
- пять views schema `autocheck`;
- C4 Container diagram;
- ADR о trust boundary Оркестратора;
- ADR о техническом и предметном результате;
- README с запуском и проверкой.

## Оформление сдачи

До сдачи добавьте в корневой `README.md` отдельную секцию второго уровня `Решение`. Исходный текст задания можно оставить ниже или перенести в `TASK.md`.

Внутри секции `Решение` обязательны подразделы третьего уровня:

- `Архитектура` — ответственность `gateway`, `api`, `cli`, `postgres` и направление вызовов;
- `Запуск` — prerequisites, команда `docker compose up -d --build`, адрес `http://localhost:8080` и ожидаемый результат;
- `Конфигурация` — используемые переменные, включая `COURSE_JWT_ISSUER`, без реальных секретов;
- `Миграции` — когда и каким сервисом они применяются, почему после `docker compose up` не нужны ручные SQL-команды;
- `Проверка` — команда `./check.sh`, краткое описание проверяемых инвариантов и, при наличии, команды собственных tests;
- `Диагностика` — как проверить `gateway`, `api`, PostgreSQL, `/health/live`, `/health/ready` и `/openapi/default.json`;
- `Ограничения` — известные ограничения и принятые технические решения.

Требования к репозиторию:

- все команды из README выполняются из корня чистого clone;
- ссылки в README относительные или доступны проверяющему;
- в Git нет `.env`, реальных секретов, `bin`, `obj`, каталогов IDE, логов и сгенерированного `week-1-public-report.json`;
- временные файлы и результаты сборки перечислены в `.gitignore`;
- схема, диаграмма C4 и два ADR находятся в репозитории и открываются по ссылкам из секции `Решение`;
- запуск и проверка не требуют локально установленных .NET SDK, PostgreSQL или ручной настройки после `docker compose up`.

Открытая и скрытая проверки сначала выполняют admission checks: наличие секции `Решение`, перечисленных подразделов и команд, Git commit, безопасный состав репозитория, Compose-сервисы и их порты. Провал admission checks останавливает функциональный прогон и не позволяет получить зачёт.

## Частые ошибки до сдачи

- `localhost` внутри контейнера указывает на этот же контейнер; `gateway` должен обращаться к `api` по Compose DNS.
- `depends_on` задаёт порядок запуска, но не доказывает readiness зависимости.
- Swagger UI не заменяет обязательные JSON endpoints OpenAPI.
- Двадцать успешных HTTP-ответов не доказывают идемпотентность; проверяется одна operation и одно начальное событие в PostgreSQL.
- Рабочий запуск на уже настроенной машине не заменяет запуск из чистого clone.
- Пример реализации не становится обязательным требованием, если он явно не находится в обязательном контракте или admission checks.
- Перед отправкой ссылки проверьте доступ к приватному репозиторию и убедитесь, что куратору передан именно полный SHA проверяемого коммита.

## Открытая проверка

В публикационном пакете есть фиксированный canary action. После запуска решения:

```bash
./check.sh
```

Команда вызывает доверенную копию `autocheck/public_check.py` из публикационного пакета.

Открытая проверка:

1. собирает и запускает Compose;
2. проверяет, что внешний порт принадлежит `gateway`, а `api` доступен только внутри Compose;
3. применяет canary migration через сервис `cli`;
4. публикует две версии неизвестного C# коду action;
5. проверяет default/explicit version, activate и disable;
6. проверяет JWT, policy, schemas, outcome и rollback;
7. выполняет конкурентный `payment.request`;
8. сверяет operation, event, dispatch и OpenAPI;
9. пересоздает `gateway` и `api`, проверяет health semantics и повторно читает operation;
10. проверяет, что runtime logs не содержат JWT и signing key.

Скрипт открытой проверки является обязательным contract check. Собственные tests рекомендуются, но их отсутствие не останавливает admission первой недели; отдельный критерий воспроизводимых tests появляется в итоговой части курса.

## Проверка после дедлайна

Закрытый контур проверяет те же опубликованные контракты на новых данных, именах actions и конкурентных interleavings. Внутренние fixtures, сценарии и система оценки не публикуются.

## Условия незачёта

Для первой недели критичны:

- контур не запускается по README;
- основной API не на C#;
- авторитетное состояние не в PostgreSQL;
- предметные endpoints реализованы отдельными C# controllers без action catalog;
- клиент может выбрать БД, схему, функцию или SQL;
- повтор создаёт новый предметный эффект;
- состояние и событие расходятся;
- скрытый action требует пересборки API;
- в репозитории или журналах есть реальные секреты.

## Не входит в неделю

- workflow maps и process instances;
- реализация worker;
- `payment.submit`;
- provider-simulator;
- Outbox/Inbox;
- receipt и manual actions;
- lease, retries и failpoints;
- отдельные C# handlers/controllers для предметных actions;
- Consul-конфигурация, CORS-трансформации, micro-cache, rate limiting и защитные очереди gateway.

Все перечисленное будет добавляться поверх результата этой недели. Не проектируйте его заранее ценой усложнения текущего задания.
