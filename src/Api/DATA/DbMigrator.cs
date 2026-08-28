using Microsoft.Extensions.Logging;
using Npgsql;

namespace Api.DATA
{
    public static class DbMigrator
    {
        public static async Task MigrateAsync(
            string connStr,
            ILogger logger)
        {
            var migrationConnStr =
                Environment.GetEnvironmentVariable(
                    "MIGRATION_CONNECTION_STRING");

            if (string.IsNullOrWhiteSpace(migrationConnStr))
            {
                throw new InvalidOperationException(
                    "MIGRATION_CONNECTION_STRING is required.");
            }

            const int maxAttempts = 30;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await using var conn =
                        new NpgsqlConnection(migrationConnStr);

                    await conn.OpenAsync();

                    var migrationsDir =
                        Path.Combine(
                            AppContext.BaseDirectory,
                            "Migrations");

                    if (!Directory.Exists(migrationsDir))
                    {
                        throw new DirectoryNotFoundException(
                            $"Migrations directory not found: {migrationsDir}");
                    }

                    var files = Directory
                        .GetFiles(migrationsDir, "*.sql")
                        .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                        .ToList();

                    if (files.Count == 0)
                    {
                        logger.LogWarning(
                            "No migration files found in {Directory}",
                            migrationsDir);

                        return;
                    }

                    foreach (var file in files)
                    {
                        var sql =
                            await File.ReadAllTextAsync(file);

                        if (string.IsNullOrWhiteSpace(sql))
                        {
                            logger.LogWarning(
                                "Skipping empty migration file {File}",
                                Path.GetFileName(file));

                            continue;
                        }

                        await using var tx =
                            await conn.BeginTransactionAsync();

                        try
                        {
                            await using var cmd =
                                new NpgsqlCommand(
                                    sql,
                                    conn,
                                    tx);

                            await cmd.ExecuteNonQueryAsync();

                            await tx.CommitAsync();

                            logger.LogInformation(
                                "Applied migration {Migration}",
                                Path.GetFileName(file));
                        }
                        catch
                        {
                            await tx.RollbackAsync();
                            throw;
                        }
                    }

                    logger.LogInformation(
                        "DB migrated with {Count} files",
                        files.Count);

                    return;
                }
                catch (Exception ex)
                    when (attempt < maxAttempts)
                {
                    logger.LogWarning(
                        ex,
                        "DB migration attempt {Attempt}/{MaxAttempts} failed",
                        attempt,
                        maxAttempts);

                    await Task.Delay(
                        TimeSpan.FromSeconds(2));
                }
            }

            throw new InvalidOperationException(
                $"Database migration failed after {maxAttempts} attempts.");
        }
    }
}