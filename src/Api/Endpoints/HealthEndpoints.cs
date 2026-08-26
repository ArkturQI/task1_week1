using Npgsql;

namespace Api.Endpoints;

public static class HealthEndpoints
{
    public static void Map(WebApplication app, string connStr)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "api" }));

        app.MapGet("/health/ready", async () =>
        {
            try
            {
                var sb = new NpgsqlConnectionStringBuilder(connStr) { Pooling = false, Timeout = 2, CommandTimeout = 2 };
                await using var conn = new NpgsqlConnection(sb.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync();
                return Results.Ok(new { status = "ok", service = "api", db = "up" });
            }
            catch
            {
                return Results.Json(new { status = "degraded", service = "api", db = "down" }, statusCode: 503);
            }
        });
    }
}