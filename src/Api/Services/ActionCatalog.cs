using System.Text.Json;
using Npgsql;

namespace Api.Services;

public sealed class ActionDef
{
    public string Module { get; init; } = "";
    public string Action { get; init; } = "";
    public int Version { get; init; }
    public JsonElement RequestSchema { get; init; }
    public JsonElement ResponseSchema { get; init; }
}

public sealed class ActionCatalog
{
    private readonly string _connStr;

    public ActionCatalog(string connStr) => _connStr = connStr;

    public async Task<List<ActionDef>> LoadAsync(CancellationToken ct)
    {
        var list = new List<ActionDef>();
        await using var conn = new NpgsqlConnection(_connStr);
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
}