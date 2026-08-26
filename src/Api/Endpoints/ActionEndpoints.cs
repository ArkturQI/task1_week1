using System.Text;
using System.Text.Json;
using Api.Services;
using Npgsql;

namespace Api.Endpoints;

public static class ActionEndpoints
{
    public static void Map(WebApplication app, string connStr, JwtValidator jwtValidator)
    {
        app.MapPost("/api/{module}/{action}", async (string module, string action, HttpContext http, CancellationToken ct) =>
        {
            var jwt = jwtValidator.Validate(http.Request.Headers.Authorization.FirstOrDefault(), out var authError);
            if (jwt is null)
                return Results.Json(new { status = "error", code = "auth.invalid", message = authError }, statusCode: 401);

            var principal = jwt.Subject ?? "";
            var consumer = jwt.Claims.FirstOrDefault(c => c.Type == "consumer")?.Value;
            var scopeClaim = jwt.Claims.FirstOrDefault(c => c.Type == "scope")?.Value;
            var scopes = string.IsNullOrWhiteSpace(scopeClaim) ? Array.Empty<string>() : scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            string body;
            using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) body = "{}";

            try { using var _ = JsonDocument.Parse(body); }
            catch { return Results.Json(new { status = "error", code = "request.invalid", message = "body is not valid JSON" }, statusCode: 400); }

            int? version = null;
            if (http.Request.Headers.TryGetValue("X-Action-Version", out var vh) && int.TryParse(vh, out var vv))
                version = vv;

            var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();

            ActionDef? actionDef = null;
            try
            {
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                // ИСПРАВЛЕНО: убрано _tbl
                var sql = version.HasValue
                    ? "SELECT module, action, version, manifest::text, enabled, is_default FROM autocheck.action_definitions WHERE module = @m AND action = @a AND version = @v AND enabled LIMIT 1"
                    : "SELECT module, action, version, manifest::text, enabled, is_default FROM autocheck.action_definitions WHERE module = @m AND action = @a AND is_default = true AND enabled LIMIT 1";
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("m", module);
                cmd.Parameters.AddWithValue("a", action);
                if (version.HasValue) cmd.Parameters.AddWithValue("v", version.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var manifest = JsonDocument.Parse(reader.GetString(3));
                    actionDef = new ActionDef
                    {
                        Module = reader.GetString(0),
                        Action = reader.GetString(1),
                        Version = reader.GetInt32(2),
                        Enabled = reader.GetBoolean(4),
                        IsDefault = reader.GetBoolean(5),
                        Manifest = manifest,
                        RequestSchema = manifest.RootElement.TryGetProperty("request_schema", out var reqProp) ? reqProp.Clone() : default,
                        ResponseSchema = manifest.RootElement.TryGetProperty("response_schema", out var resProp) ? resProp.Clone() : default
                    };
                }
            }
            catch (NpgsqlException)
            {
                return Results.Json(new { status = "error", code = "dependency.unavailable", message = "database unavailable" }, statusCode: 503);
            }

            if (actionDef is null)
            {
                return Results.Json(
                    new { status = "error", code = "action.not_found", message = "action not found", meta = new { correlationId = Guid.NewGuid().ToString(), actionVersion = version } },
                    statusCode: 404);
            }

            var timeoutMs = actionDef.Manifest.RootElement.TryGetProperty("timeout_ms", out var tms) && tms.TryGetInt32(out var tmsi) ? tmsi : 10000;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var correlationId = Guid.NewGuid().ToString();

            var context = new System.Text.Json.Nodes.JsonObject
            {
                ["principal"] = principal,
                ["consumer"] = consumer,
                ["scopes"] = new System.Text.Json.Nodes.JsonArray(scopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
                ["correlationId"] = correlationId,
                ["requestId"] = idempotencyKey ?? Guid.NewGuid().ToString(),
                ["deadline"] = deadline.ToString("O")
            };
            if (!string.IsNullOrEmpty(idempotencyKey)) context["idempotencyKey"] = idempotencyKey;

            var idempotencyMode = actionDef.Manifest.RootElement.TryGetProperty("idempotency_mode", out var im) ? im.GetString() : "none";
            if (idempotencyMode == "required" && string.IsNullOrEmpty(idempotencyKey))
            {
                return Results.Json(
                    new { status = "error", code = "idempotency.required", message = "Idempotency-Key header is required", meta = new { correlationId, actionVersion = actionDef.Version } },
                    statusCode: 400);
            }

            var requestSchema = actionDef.RequestSchema;
            if (requestSchema.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync(ct);
                    await using var cmd = new NpgsqlCommand("SELECT api.json_schema_validate(@schema::jsonb, @payload::jsonb)::text", conn);
                    cmd.Parameters.AddWithValue("schema", requestSchema.GetRawText());
                    cmd.Parameters.AddWithValue("payload", body);
                    var validationRaw = await cmd.ExecuteScalarAsync(ct) as string ?? "{}";
                    using var vdoc = JsonDocument.Parse(validationRaw);
                    if (vdoc.RootElement.TryGetProperty("valid", out var valid) && valid.GetBoolean() == false)
                    {
                        return Results.Json(
                            new { status = "error", code = "payload.invalid", message = vdoc.RootElement.TryGetProperty("error", out var verr) ? verr.GetString() : "invalid payload", meta = new { correlationId, actionVersion = actionDef.Version } },
                            statusCode: 422);
                    }
                }
                catch (NpgsqlException)
                {
                    return Results.Json(new { status = "error", code = "dependency.unavailable", message = "database unavailable" }, statusCode: 503);
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                return Results.Json(
                    new { status = "error", code = "action.timeout", message = "deadline exceeded", meta = new { correlationId, actionVersion = actionDef.Version } },
                    statusCode: 504);
            }

            string raw;
            try
            {
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT api.invoke(@m, @a, @v, @ctx::jsonb, @p::jsonb)::text", conn, tx);
                cmd.Parameters.AddWithValue("m", module);
                cmd.Parameters.AddWithValue("a", action);
                cmd.Parameters.AddWithValue("v", version is null ? DBNull.Value : version);
                cmd.Parameters.AddWithValue("ctx", context.ToJsonString());
                cmd.Parameters.AddWithValue("p", body);

                var scalar = await cmd.ExecuteScalarAsync(ct) as string ?? "{}";
                raw = scalar;

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var s) && s.GetString() == "error")
                {
                    await tx.RollbackAsync(ct);
                    var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "internal.error" : "internal.error";
                    var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                    var metaVersion = code == "action.not_found" ? version : actionDef.Version;

                    return Results.Json(new Dictionary<string, object?>
                    {
                        ["status"] = "error",
                        ["code"] = code,
                        ["message"] = message,
                        ["retryable"] = MapHttpCode(code) >= 500,
                        ["details"] = new Dictionary<string, object>(),
                        ["meta"] = new Dictionary<string, object?> { ["correlationId"] = correlationId, ["actionVersion"] = metaVersion }
                    }, statusCode: MapHttpCode(code));
                }

                var outcomes = actionDef.Manifest.RootElement.TryGetProperty("outcomes", out var oc) ? oc : default;
                var outcomeStr = root.TryGetProperty("outcome", out var o) ? o.GetString() : null;
                if (outcomeStr is null || (outcomes.ValueKind == JsonValueKind.Array && !outcomes.EnumerateArray().Any(x => x.GetString() == outcomeStr)))
                {
                    await tx.RollbackAsync(ct);
                    return Results.Json(
                        new { status = "error", code = "action.contract_violation", message = "undeclared outcome", meta = new { correlationId, actionVersion = actionDef.Version } },
                        statusCode: 500);
                }

                var responseSchema = actionDef.ResponseSchema;
                if (responseSchema.ValueKind == JsonValueKind.Object)
                {
                    await using var cmd2 = new NpgsqlCommand("SELECT api.json_schema_validate(@schema::jsonb, @result::jsonb)::text", conn, tx);
                    cmd2.Parameters.AddWithValue("schema", responseSchema.GetRawText());
                    var resultElement = root.TryGetProperty("result", out var re) ? re.GetRawText() : "{}";
                    cmd2.Parameters.AddWithValue("result", resultElement);
                    var validationRaw = await cmd2.ExecuteScalarAsync(ct) as string ?? "{}";
                    using var vdoc = JsonDocument.Parse(validationRaw);
                    if (vdoc.RootElement.TryGetProperty("valid", out var valid) && valid.GetBoolean() == false)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Json(
                            new { status = "error", code = "action.contract_violation", message = vdoc.RootElement.TryGetProperty("error", out var verr) ? verr.GetString() : "invalid result", meta = new { correlationId, actionVersion = actionDef.Version } },
                            statusCode: 500);
                    }
                }

                await tx.CommitAsync(ct);

                var finalResponse = new Dictionary<string, object?>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "meta")
                    {
                        var meta = new Dictionary<string, object?> { ["correlationId"] = correlationId, ["actionVersion"] = actionDef.Version };
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                            foreach (var mp in prop.Value.EnumerateObject())
                                if (!meta.ContainsKey(mp.Name)) meta[mp.Name] = mp.Value.Clone();
                        finalResponse["meta"] = meta;
                    }
                    else
                    {
                        finalResponse[prop.Name] = prop.Value.Clone();
                    }
                }
                return Results.Json(finalResponse, statusCode: 200);
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
            Results.Json(new { status = "error", code = "route.not_found", message = "route not found" }, statusCode: 404));
    }

    private static int MapHttpCode(string code) => code switch
    {
        "action.not_found" => 404,
        "operation.not_found" => 404,
        "access.denied" => 403,
        "payload.invalid" => 422,
        "idempotency.required" => 400,
        "idempotency.conflict" => 409,
        "action.contract_violation" => 500,
        "dependency.unavailable" => 503,
        "request.invalid" => 400,
        "action.timeout" => 504,
        "internal.error" => 500,
        _ => 500
    };
}