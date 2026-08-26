using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var connStr = builder.Configuration.GetConnectionString("Course")
    ?? "Host=postgres;Port=5432;Database=course;Username=postgres;Password=postgres";

var jwtIssuer = builder.Configuration["COURSE_JWT_ISSUER"] ?? "moduledev-course";
var jwtAudience = builder.Configuration["COURSE_JWT_AUDIENCE"] ?? "moduledev-api";
var jwtSigningKey = builder.Configuration["COURSE_JWT_SIGNING_KEY"] ?? "this-is-a-very-long-and-secure-secret-key-for-jwt-signing-at-least-32-bytes";

await Api.DATA.DbMigrator.MigrateAsync(connStr, app.Logger);

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "api" }));

app.MapGet("/health/ready", async () =>
{
    try
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        return Results.Ok(new { status = "ok", service = "api", db = "up" });
    }
    catch
    {
        return Results.Json(new { status = "degraded", db = "down" }, statusCode: 503);
    }
});

app.MapGet("/openapi/default.json", async (CancellationToken ct) =>
{
    try
    {
        var defs = await LoadDefinitionsAsync(ct);
        var paths = new Dictionary<string, object>();
        foreach (var d in defs)
        {
            var path = $"/api/{d.Module}/{d.Action}";
            if (!paths.ContainsKey(path)) paths[path] = BuildPathItem(d);
        }
        return Results.Ok(BuildDocument(paths));
    }
    catch (NpgsqlException)
    {
        return Results.Json(new { status = "error", code = "dependency.unavailable" }, statusCode: 503);
    }
});

app.MapGet("/openapi/actions/{module}/{action}/{version:int}.json", async (string module, string action, int version, CancellationToken ct) =>
{
    try
    {
        var defs = await LoadDefinitionsAsync(ct);
        var d = defs.FirstOrDefault(x => x.Module == module && x.Action == action && x.Version == version);
        if (d is null)
            return Results.Json(new { status = "error", code = "action.not_found" }, statusCode: 404);
        var paths = new Dictionary<string, object> { [$"/api/{module}/{action}"] = BuildPathItem(d) };
        return Results.Ok(BuildDocument(paths));
    }
    catch (NpgsqlException)
    {
        return Results.Json(new { status = "error", code = "dependency.unavailable" }, statusCode: 503);
    }
});

app.MapPost("/api/payment/request", (HttpContext http, CancellationToken ct) =>
    HandleDirectAsync(http, "payment:write", "payment_request", ct));

app.MapPost("/api/operation/get", (HttpContext http, CancellationToken ct) =>
    HandleDirectAsync(http, "payment:read", "operation_get", ct));

app.MapPost("/api/{module}/{action}", async (string module, string action, HttpContext http, CancellationToken ct) =>
{
    var jwt = ValidateJwt(http, out var authError);
    if (jwt is null) return authError!;

    var principal = jwt.Subject ?? "";
    var consumer = jwt.Claims.FirstOrDefault(c => c.Type == "consumer")?.Value;
    var scopeClaim = jwt.Claims.FirstOrDefault(c => c.Type == "scope")?.Value;
    var scopes = string.IsNullOrWhiteSpace(scopeClaim)
        ? Array.Empty<string>()
        : scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    string body;
    using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
    {
        body = await reader.ReadToEndAsync(ct);
    }
    if (string.IsNullOrWhiteSpace(body)) body = "{}";

    try { using var probe = JsonDocument.Parse(body); }
    catch { return Results.Json(new { status = "error", code = "payload.invalid", message = "body is not valid JSON" }, statusCode: 422); }

    int? version = null;
    if (http.Request.Headers.TryGetValue("X-Action-Version", out var vh) && int.TryParse(vh, out var vv))
        version = vv;

    var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();

    var context = new System.Text.Json.Nodes.JsonObject
    {
        ["principal"] = principal,
        ["consumer"] = consumer,
        ["scopes"] = new System.Text.Json.Nodes.JsonArray(scopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
        ["correlationId"] = Guid.NewGuid().ToString(),
        ["requestId"] = Guid.NewGuid().ToString(),
        ["deadline"] = DateTime.UtcNow.AddSeconds(10).ToString("O")
    };
    if (!string.IsNullOrEmpty(idempotencyKey)) context["idempotencyKey"] = idempotencyKey;

    try
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT api.invoke(@m, @a, @v, @ctx::jsonb, @p::jsonb)::text", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("v", version is null ? DBNull.Value : version);
        cmd.Parameters.AddWithValue("ctx", context.ToJsonString());
        cmd.Parameters.AddWithValue("p", body);

        var raw = await cmd.ExecuteScalarAsync(ct) as string ?? "{}";
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var s) && s.GetString() == "ok")
            return Results.Content(raw, "application/json");

        var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "internal.error" : "internal.error";
        return Results.Content(raw, "application/json", statusCode: MapHttpCode(code));
    }
    catch (NpgsqlException)
    {
        return Results.Json(new { status = "error", code = "dependency.unavailable", message = "database unavailable" }, statusCode: 503);
    }
    catch (Exception)
    {
        return Results.Json(new { status = "error", code = "internal.error", message = "unexpected error" }, statusCode: 500);
    }
});

app.MapMethods("/{**rest}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH" }, () =>
    Results.Json(new { code = "route.not_found" }, statusCode: 404));

app.Run();

JwtSecurityToken? ValidateJwt(HttpContext http, out IResult? error)
{
    error = null;
    var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        error = Results.Json(new { status = "error", code = "auth.invalid", message = "missing bearer token" }, statusCode: 401);
        return null;
    }

    var token = authHeader["Bearer ".Length..].Trim();
    try
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        handler.ValidateToken(token, validationParams, out var validatedToken);
        return validatedToken as JwtSecurityToken
            ?? throw new SecurityTokenException("token is not a JWT");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("JWT validation failed: {Message}", ex.Message);
        error = Results.Json(new { status = "error", code = "auth.invalid", message = "token validation failed" }, statusCode: 401);
        return null;
    }
}

async Task<IResult> HandleDirectAsync(HttpContext http, string requiredScope, string functionName, CancellationToken ct)
{
    var jwt = ValidateJwt(http, out var authError);
    if (jwt is null) return authError!;

    var scopeClaim = jwt.Claims.FirstOrDefault(c => c.Type == "scope")?.Value;
    var scopes = string.IsNullOrWhiteSpace(scopeClaim)
        ? Array.Empty<string>()
        : scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    if (!scopes.Contains(requiredScope))
        return Results.Json(new { status = "error", code = "access.denied", message = $"missing scope {requiredScope}" }, statusCode: 403);

    string body;
    using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
    {
        body = await reader.ReadToEndAsync(ct);
    }
    if (string.IsNullOrWhiteSpace(body)) body = "{}";

    try { using var probe = JsonDocument.Parse(body); }
    catch { return Results.Json(new { status = "error", code = "payload.invalid", message = "body is not valid JSON" }, statusCode: 422); }

    var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();

    var context = new System.Text.Json.Nodes.JsonObject
    {
        ["principal"] = jwt.Subject ?? "",
        ["consumer"] = jwt.Claims.FirstOrDefault(c => c.Type == "consumer")?.Value,
        ["scopes"] = new System.Text.Json.Nodes.JsonArray(scopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
        ["correlationId"] = Guid.NewGuid().ToString(),
        ["requestId"] = idempotencyKey ?? Guid.NewGuid().ToString()
    };
    if (!string.IsNullOrEmpty(idempotencyKey)) context["idempotencyKey"] = idempotencyKey;

    try
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT api.{functionName}(@ctx::jsonb, @p::jsonb)::text", conn);
        cmd.Parameters.AddWithValue("ctx", context.ToJsonString());
        cmd.Parameters.AddWithValue("p", body);

        var raw = await cmd.ExecuteScalarAsync(ct) as string ?? "{}";
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var s) && s.GetString() == "ok")
            return Results.Content(raw, "application/json");

        var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "internal.error" : "internal.error";
        return Results.Content(raw, "application/json", statusCode: MapHttpCode(code));
    }
    catch (NpgsqlException)
    {
        return Results.Json(new { status = "error", code = "dependency.unavailable", message = "database unavailable" }, statusCode: 503);
    }
    catch (Exception)
    {
        return Results.Json(new { status = "error", code = "internal.error", message = "unexpected error" }, statusCode: 500);
    }
}

static int MapHttpCode(string code) => code switch
{
    "action.not_found" => 404,
    "operation.not_found" => 404,
    "access.denied" => 403,
    "payload.invalid" => 422,
    "idempotency.required" => 400,
    "idempotency.conflict" => 409,
    "action.contract_violation" => 422,
    "dependency.unavailable" => 503,
    _ => 409
};

static object BuildPathItem(ActionDef d)
{
    var requestBody = new Dictionary<string, object>
    {
        ["required"] = true,
        ["content"] = new Dictionary<string, object>
        {
            ["application/json"] = new Dictionary<string, object>
            {
                ["schema"] = d.RequestSchema.ValueKind == JsonValueKind.Object
                    ? (object)d.RequestSchema.Clone()
                    : new Dictionary<string, object>()
            }
        }
    };

    var responses = new Dictionary<string, object>
    {
        ["200"] = new Dictionary<string, object>
        {
            ["description"] = "successful operation",
            ["content"] = new Dictionary<string, object>
            {
                ["application/json"] = new Dictionary<string, object>
                {
                    ["schema"] = d.ResponseSchema.ValueKind == JsonValueKind.Object
                        ? (object)d.ResponseSchema.Clone()
                        : new Dictionary<string, object>()
                }
            }
        }
    };

    return new Dictionary<string, object>
    {
        ["post"] = new Dictionary<string, object>
        {
            ["operationId"] = $"{d.Module}_{d.Action}_v{d.Version}",
            ["requestBody"] = requestBody,
            ["responses"] = responses
        }
    };
}

static object BuildDocument(Dictionary<string, object> paths) => new Dictionary<string, object>
{
    ["openapi"] = "3.1.0",
    ["info"] = new Dictionary<string, object>
    {
        ["title"] = "moduledev week-1 action gateway",
        ["version"] = "course-1"
    },
    ["paths"] = paths
};

async Task<List<ActionDef>> LoadDefinitionsAsync(CancellationToken ct)
{
    var list = new List<ActionDef>();
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(
        "SELECT module, action, version, manifest::text FROM autocheck.action_definitions ORDER BY module, action, version", conn);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        using var manifest = JsonDocument.Parse(reader.GetString(3));
        list.Add(new ActionDef
        {
            Module = reader.GetString(0),
            Action = reader.GetString(1),
            Version = reader.GetInt32(2),
            RequestSchema = manifest.RootElement.TryGetProperty("request_schema", out var rs) ? rs.Clone() : default,
            ResponseSchema = manifest.RootElement.TryGetProperty("response_schema", out var ss) ? ss.Clone() : default
        });
    }
    return list;
}

internal sealed class ActionDef
{
    public string Module { get; init; } = "";
    public string Action { get; init; } = "";
    public int Version { get; init; }
    public JsonElement RequestSchema { get; init; }
    public JsonElement ResponseSchema { get; init; }
}