using Cli.Services;
using Npgsql;

namespace Cli.Commands;

internal static class MigrationCommands
{
    public static async Task<int> ApplyAsync(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Console.WriteLine(Envelope.Error("migration.invalid_directory", "directory not found: " + dir));
            return 1;
        }

        var files = Directory.GetFiles(dir, "*.sql")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name)
            .ToList();

        await using var conn = new NpgsqlConnection(Database.ConnStr());
        await conn.OpenAsync();

        var applied = 0;
        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file.FullName);
            var checksum = Database.Sha256Hex(content);

            await using var check = new NpgsqlCommand(
                "SELECT checksum FROM autocheck.schema_migrations WHERE file_name = @name", conn);
            check.Parameters.AddWithValue("name", file.Name);
            var existing = await check.ExecuteScalarAsync() as string;

            if (existing is not null)
            {
                if (existing == checksum) continue;
                Console.WriteLine(Envelope.Error("migration.checksum_conflict",
                    "file " + file.Name + " was already applied with a different content"));
                return 1;
            }

            await using var tx = await conn.BeginTransactionAsync();
            await using var run = new NpgsqlCommand(content, conn, tx);
            await run.ExecuteNonQueryAsync();

            await using var ins = new NpgsqlCommand(
                "INSERT INTO autocheck.schema_migrations(file_name, checksum) VALUES (@name, @cs)", conn, tx);
            ins.Parameters.AddWithValue("name", file.Name);
            ins.Parameters.AddWithValue("cs", checksum);
            await ins.ExecuteNonQueryAsync();
            await tx.CommitAsync();

            applied++;
        }

        Console.WriteLine(Envelope.Ok(new
        {
            resource = "migration",
            operation = "applied",
            applied,
            dir,
            files = files.Select(f => f.Name)
        }));
        return 0;
    }
}