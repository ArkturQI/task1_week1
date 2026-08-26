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
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync();
                return Results.Ok(new { status = "ok", service = "api", db = "up" });
            }
            catch
            {
                return Results.Json(new { status = "degraded", db = "down" }, statusCode: 503);
            }
        });
    }
}