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

            // Строгая валидация заголовка X-Action-Version
            int? version = null;
            if (http.Request.Headers.TryGetValue("X-Action-Version", out var vh))
            {
                var headerVal = vh.ToString();
                if (string.IsNullOrWhiteSpace(headerVal) || !int.TryParse(headerVal, out var vv) || vv < 1)
                {
                    return Results.Json(
                        new { status = "error", code = "request.invalid", message = "X-Action-Version must be a positive integer", meta = new { correlationId = Guid.NewGuid().ToString() } },
                        statusCode: 400);
                }
                version = vv;
            }

            var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var correlationId = Guid.NewGuid().ToString();

            var context = new System.Text.Json.Nodes.JsonObject
            {
                ["principal"] = principal,
                ["consumer"] = consumer,
                ["scopes"] = new System.Text.Json.Nodes.JsonArray(scopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
                ["correlationId"] = correlationId,
                ["requestId"] = idempotencyKey ?? Guid.NewGuid().ToString()
            };
            if (!string.IsNullOrEmpty(idempotencyKey)) context["idempotencyKey"] = idempotencyKey;

            try
            {
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT api.invoke(@m, @a, @v, @ctx::jsonb, @p::jsonb)::text", conn, tx);
                cmd.CommandTimeout = 30;
                cmd.Parameters.AddWithValue("m", module);
                cmd.Parameters.AddWithValue("a", action);
                cmd.Parameters.AddWithValue("v", version.HasValue ? version.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("ctx", context.ToJsonString());
                cmd.Parameters.AddWithValue("p", body);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                var raw = await cmd.ExecuteScalarAsync(cts.Token) as string ?? "{}";
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var s) && s.GetString() == "error")
                {
                    await tx.RollbackAsync(ct);
                    var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "internal.error" : "internal.error";
                    var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                    var errorDict = new Dictionary<string, object?>
                    {
                        ["status"] = "error",
                        ["code"] = code,
                        ["message"] = message,
                        ["retryable"] = MapHttpCode(code) >= 500,
                        ["details"] = new Dictionary<string, object>(),
                        ["meta"] = new Dictionary<string, object?> { ["correlationId"] = correlationId, ["actionVersion"] = version }
                    };
                    return Results.Json(errorDict, statusCode: MapHttpCode(code));
                }

                await tx.CommitAsync(ct);

                var finalResponse = new Dictionary<string, object?>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "meta")
                    {
                        var meta = new Dictionary<string, object?> { ["correlationId"] = correlationId, ["actionVersion"] = version ?? 1 };
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
            catch (OperationCanceledException)
            {
                return Results.Json(new { status = "error", code = "action.timeout", message = "request timeout", meta = new { correlationId, actionVersion = version } }, statusCode: 504);
            }
            catch (Exception)
            {
                return Results.Json(new { status = "error", code = "internal.error", message = "unexpected error", meta = new { correlationId, actionVersion = version } }, statusCode: 500);
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