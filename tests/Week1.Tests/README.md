# Week1.Tests

xUnit tests for the Week 1 gateway task.

## Before running

Start the stack:

```powershell
docker compose up -d --build
```

Apply the autocheck migration and publish the opencheck manifests:

```powershell
docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli migration apply /autocheck/input/migrations

docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v1.action.json

docker compose run --rm -T -v ${PWD}/autocheck/fixtures:/autocheck/input:ro cli action publish /autocheck/input/manifests/opencheck-probe-v2.action.json
```

Then run:

```powershell
dotnet test tests/Week1.Tests/Week1.Tests.csproj
```

The integration tests default to `http://localhost:8080` and use the same default issuer/audience/signing key as the compose configuration. Override them with:

```powershell
$env:WEEK1_BASE_URL = "http://localhost:8080"
$env:COURSE_JWT_ISSUER = "moduledev-course"
$env:COURSE_JWT_AUDIENCE = "moduledev-api"
$env:COURSE_JWT_SIGNING_KEY = "..."
```
