using Microsoft.Extensions.Logging;
using Npgsql;

namespace Api.DATA
{
    public static class DbMigrator
    {
        public static async Task MigrateAsync(string connStr, ILogger logger)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync();

                    var migrationsDir = Path.Combine(AppContext.BaseDirectory, "Migrations");
                    var files = Directory.GetFiles(migrationsDir, "*.sql")
                        .OrderBy(f => f)
                        .ToList();

                    foreach (var file in files)
                    {
                        var sql = await File.ReadAllTextAsync(file);
                        await using var cmd = new NpgsqlCommand(sql, conn);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    logger.LogInformation("DB migrated with {Count} files", files.Count);
                    return;
                }
                catch (Exception ex) when (attempt < 30)
                {
                    logger.LogWarning("DB not ready, attempt {Attempt}: {Message}", attempt, ex.Message);
                    await Task.Delay(2000);
                }
            }
        }
    }
}