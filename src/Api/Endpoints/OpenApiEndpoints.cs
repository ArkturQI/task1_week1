using System.Text.Json;
using Npgsql;

namespace Api.Endpoints;

public static class OpenApiEndpoints
{
    public static void Map(WebApplication app, string connStr)
    {
        app.MapGet("/openapi/default.json", async (CancellationToken ct) =>
        {
            try
            {
                var defs = await LoadDefinitionsAsync(connStr, ct);
                var paths = new Dictionary<string, object>();
                foreach (var d in defs.Where(x => x.Enabled && x.IsDefault))
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
                var defs = await LoadDefinitionsAsync(connStr, ct);
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
    }

    private static object BuildPathItem(ActionDef d)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["required"] = true,
            ["content"] = new Dictionary<string, object>
            {
                ["application/json"] = new Dictionary<string, object>
                {
                    ["schema"] = d.RequestSchema.ValueKind == JsonValueKind.Object ? (object)d.RequestSchema.Clone() : new Dictionary<string, object>()
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
                        ["schema"] = d.ResponseSchema.ValueKind == JsonValueKind.Object ? (object)d.ResponseSchema.Clone() : new Dictionary<string, object>()
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

    private static object BuildDocument(Dictionary<string, object> paths) => new Dictionary<string, object>
    {
        ["openapi"] = "3.1.0",
        ["info"] = new Dictionary<string, object> { ["title"] = "moduledev week-1 action gateway", ["version"] = "course-1" },
        ["paths"] = paths
    };

    private static async Task<List<ActionDef>> LoadDefinitionsAsync(string connStr, CancellationToken ct)
    {
        var list = new List<ActionDef>();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);
        // ЧИТАЕМ ИЗ _tbl, так как нам нужен manifest, которого нет в View
        await using var cmd = new NpgsqlCommand(
            "SELECT module, action, version, manifest::text, enabled, is_default FROM autocheck.action_definitions_tbl ORDER BY module, action, version", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var manifest = JsonDocument.Parse(reader.GetString(3));
            list.Add(new ActionDef
            {
                Module = reader.GetString(0),
                Action = reader.GetString(1),
                Version = reader.GetInt32(2),
                Enabled = reader.GetBoolean(4),
                IsDefault = reader.GetBoolean(5),
                Manifest = manifest,
                RequestSchema = manifest.RootElement.TryGetProperty("request_schema", out var rs) ? rs.Clone() : default,
                ResponseSchema = manifest.RootElement.TryGetProperty("response_schema", out var ss) ? ss.Clone() : default
            });
        }
        return list;
    }
}

public sealed class ActionDef
{
    public string Module { get; init; } = "";
    public string Action { get; init; } = "";
    public int Version { get; init; }
    public bool Enabled { get; init; }
    public bool IsDefault { get; init; }
    public JsonDocument Manifest { get; init; } = null!;
    public JsonElement RequestSchema { get; init; }
    public JsonElement ResponseSchema { get; init; }
}