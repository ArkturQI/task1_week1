using Cli.Models;
using Cli.Services;
using Npgsql;

namespace Cli.Commands;

internal static class ActionCommands
{
    public static int Validate(string? file)
    {
        if (!ManifestParser.TryParse(file, out var info, out var error))
        {
            Console.WriteLine(Envelope.Error("action.invalid_manifest", error!));
            return 1;
        }
        Console.WriteLine(Envelope.Ok(ValidateResult(info!)));
        return 0;
    }

    public static async Task<int> PublishAsync(string? file)
    {
        if (!ManifestParser.TryParse(file, out var info, out var error))
        {
            Console.WriteLine(Envelope.Error("action.invalid_manifest", error!));
            return 1;
        }
        var m = info!;

        await using var conn = new NpgsqlConnection(Database.ConnStr());
        await conn.OpenAsync();

        await using var sel = new NpgsqlCommand(
            "SELECT manifest_hash FROM autocheck.action_definitions WHERE module = @m AND action = @a AND version = @v", conn);
        sel.Parameters.AddWithValue("m", m.Module);
        sel.Parameters.AddWithValue("a", m.Action);
        sel.Parameters.AddWithValue("v", m.Version);
        var existing = await sel.ExecuteScalarAsync() as string;

        if (existing is not null)
        {
            if (existing == m.Hash)
            {
                Console.WriteLine(Envelope.Ok(PublishResult(m)));
                return 0;
            }
            Console.WriteLine(Envelope.Error("manifest.conflict", "published action version is immutable"));
            return 1;
        }

        await using var tx = await conn.BeginTransactionAsync();

        if (m.IsDefault)
        {
            await using var unset = new NpgsqlCommand(
                "UPDATE autocheck.action_definitions SET is_default = false WHERE module = @m AND action = @a", conn, tx);
            unset.Parameters.AddWithValue("m", m.Module);
            unset.Parameters.AddWithValue("a", m.Action);
            await unset.ExecuteNonQueryAsync();
        }

        await using var ins = new NpgsqlCommand(
            "INSERT INTO autocheck.action_definitions (module, action, version, http_method, target_schema, target_function, outcomes, manifest, manifest_hash, enabled, is_default) VALUES (@m, @a, @v, @hm, @ts, @tf, @outcomes::jsonb, @manifest::jsonb, @hash, @enabled, @is_default)", conn, tx);
        ins.Parameters.AddWithValue("m", m.Module);
        ins.Parameters.AddWithValue("a", m.Action);
        ins.Parameters.AddWithValue("v", m.Version);
        ins.Parameters.AddWithValue("hm", m.HttpMethod);
        ins.Parameters.AddWithValue("ts", m.TargetSchema);
        ins.Parameters.AddWithValue("tf", m.TargetFunction);
        ins.Parameters.AddWithValue("outcomes", m.Outcomes);
        ins.Parameters.AddWithValue("manifest", m.Content);
        ins.Parameters.AddWithValue("hash", m.Hash);
        ins.Parameters.AddWithValue("enabled", m.Enabled);
        ins.Parameters.AddWithValue("is_default", m.IsDefault);
        await ins.ExecuteNonQueryAsync();

        await tx.CommitAsync();
        Console.WriteLine(Envelope.Ok(PublishResult(m)));
        return 0;
    }

    public static async Task<int> ListAsync()
    {
        await using var conn = new NpgsqlConnection(Database.ConnStr());
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT module, action, version, enabled, is_default FROM autocheck.action_definitions ORDER BY module, action, version", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var items = new List<object>();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                module = reader.GetString(0),
                action = reader.GetString(1),
                version = reader.GetInt32(2),
                enabled = reader.GetBoolean(3),
                is_default = reader.GetBoolean(4)
            });
        }
        Console.WriteLine(Envelope.Ok(new { resource = "action", operation = "listed", items }));
        return 0;
    }

    public static async Task<int> LifecycleAsync(string op, string[] rest)
    {
        string? route = null;
        int? version = null;
        int? replacement = null;

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--version" when i + 1 < rest.Length:
                    version = int.Parse(rest[++i]);
                    break;
                case "--replacement-version" when i + 1 < rest.Length:
                    replacement = int.Parse(rest[++i]);
                    break;
                default:
                    route = rest[i];
                    break;
            }
        }

        if (route is null || !route.Contains('.'))
        {
            Console.WriteLine(Envelope.Error("action.invalid_arguments", "route key 'module.action' is required"));
            return 1;
        }
        if (version is null)
        {
            Console.WriteLine(Envelope.Error("action.invalid_arguments", "--version is required"));
            return 1;
        }

        var dot = route.IndexOf('.');
        var module = route[..dot];
        var action = route[(dot + 1)..];

        await using var conn = new NpgsqlConnection(Database.ConnStr());
        await conn.OpenAsync();

        if (op == "activate")
        {
            await using var tx = await conn.BeginTransactionAsync();

            await using var guard = new NpgsqlCommand(
                "SELECT count(*) FROM autocheck.action_definitions WHERE module = @m AND action = @a AND version = @v AND enabled", conn, tx);
            guard.Parameters.AddWithValue("m", module);
            guard.Parameters.AddWithValue("a", action);
            guard.Parameters.AddWithValue("v", version.Value);
            var found = (long)(await guard.ExecuteScalarAsync())!;
            if (found == 0)
            {
                Console.WriteLine(Envelope.Error("action.not_found", $"enabled action {module}.{action} version {version} not found"));
                return 1;
            }

            await using var upd = new NpgsqlCommand(
                "UPDATE autocheck.action_definitions SET is_default = (version = @v) WHERE module = @m AND action = @a AND enabled = true", conn, tx);
            upd.Parameters.AddWithValue("m", module);
            upd.Parameters.AddWithValue("a", action);
            upd.Parameters.AddWithValue("v", version.Value);
            await upd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            Console.WriteLine(Envelope.Ok(new
            {
                resource = "action",
                operation = "activated",
                key = $"{module}.{action}",
                version = version.Value
            }));
            return 0;
        }

        await using var tx2 = await conn.BeginTransactionAsync();

        await using var dis = new NpgsqlCommand(
            "UPDATE autocheck.action_definitions SET enabled = false, is_default = false WHERE module = @m AND action = @a AND version = @v", conn, tx2);
        dis.Parameters.AddWithValue("m", module);
        dis.Parameters.AddWithValue("a", action);
        dis.Parameters.AddWithValue("v", version.Value);
        var affected = await dis.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            Console.WriteLine(Envelope.Error("action.not_found", $"action {module}.{action} version {version} not found"));
            return 1;
        }

        if (replacement is not null)
        {
            await using var rep = new NpgsqlCommand(
                "UPDATE autocheck.action_definitions SET is_default = true WHERE module = @m AND action = @a AND version = @r AND enabled", conn, tx2);
            rep.Parameters.AddWithValue("m", module);
            rep.Parameters.AddWithValue("a", action);
            rep.Parameters.AddWithValue("r", replacement.Value);
            var repAffected = await rep.ExecuteNonQueryAsync();
            if (repAffected == 0)
            {
                Console.WriteLine(Envelope.Error("action.not_found", $"enabled replacement version {replacement} not found"));
                return 1;
            }
        }

        await tx2.CommitAsync();
        Console.WriteLine(Envelope.Ok(new
        {
            resource = "action",
            operation = "disabled",
            key = $"{module}.{action}",
            version = version.Value,
            replacement
        }));
        return 0;
    }

    private static object ValidateResult(ManifestInfo m) => new
    {
        resource = "action",
        operation = "validated",
        key = $"{m.Module}.{m.Action}",
        version = m.Version,
        hash = m.Hash
    };

    private static object PublishResult(ManifestInfo m) => new
    {
        resource = "action",
        operation = "published",
        key = $"{m.Module}.{m.Action}",
        version = m.Version
    };
}