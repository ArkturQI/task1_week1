using Cli.Models;
using Cli.Services;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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
            """
            SELECT manifest_hash
            FROM autocheck.action_definitions
            WHERE module = @m
              AND action = @a
              AND version = @v
            """,
            conn);

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

            Console.WriteLine(
                Envelope.Error(
                    "manifest.conflict",
                    "published action version is immutable"));

            return 1;
        }

        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            /*
             * Если новая версия становится default,
             * сначала снимаем default с остальных версий.
             *
             * Это выполняется в одной transaction.
             */
            if (m.IsDefault)
            {
                await using var unset = new NpgsqlCommand(
                    """
                    UPDATE autocheck.action_definitions
                    SET is_default = false
                    WHERE module = @m
                      AND action = @a
                      AND is_default = true
                    """,
                    conn,
                    tx);

                unset.Parameters.AddWithValue("m", m.Module);
                unset.Parameters.AddWithValue("a", m.Action);

                await unset.ExecuteNonQueryAsync();
            }

            await using var ins = new NpgsqlCommand(
                """
                INSERT INTO autocheck.action_definitions
                (
                    module,
                    action,
                    version,
                    http_method,
                    target_schema,
                    target_function,
                    outcomes,
                    manifest,
                    manifest_hash,
                    enabled,
                    is_default
                )
                VALUES
                (
                    @m,
                    @a,
                    @v,
                    @hm,
                    @ts,
                    @tf,
                    @outcomes::jsonb,
                    @manifest::jsonb,
                    @hash,
                    @enabled,
                    @is_default
                )
                """,
                conn,
                tx);

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

            // The published target is executed by api.invoke as api_owner.
            // Grant only the minimum privileges required for the exact target.
            await using var targetExists = new NpgsqlCommand(
                """
                SELECT to_regprocedure(
                    format('%I.%I(jsonb,jsonb)', @schema, @function)
                ) IS NOT NULL
                """,
                conn,
                tx);

            targetExists.Parameters.AddWithValue("schema", m.TargetSchema);
            targetExists.Parameters.AddWithValue("function", m.TargetFunction);

            var targetExistsValue =
                await targetExists.ExecuteScalarAsync();

            if (targetExistsValue is not bool exists || !exists)
            {
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "action.target_not_found",
                        $"target function {m.TargetSchema}.{m.TargetFunction}(jsonb,jsonb) not found"));

                return 1;
            }

            await using var grantSchema = new NpgsqlCommand(
                $"GRANT USAGE ON SCHEMA {m.TargetSchema} TO api_owner",
                conn,
                tx);

            await grantSchema.ExecuteNonQueryAsync();

            await using var grantFunction = new NpgsqlCommand(
                $"GRANT EXECUTE ON FUNCTION {m.TargetSchema}.{m.TargetFunction}(jsonb, jsonb) TO api_owner",
                conn,
                tx);

            await grantFunction.ExecuteNonQueryAsync();

            await tx.CommitAsync();

            Console.WriteLine(Envelope.Ok(PublishResult(m)));
            return 0;
        }
        catch (PostgresException ex)
        {
            await tx.RollbackAsync();

            if (ex.SqlState == "23505")
            {
                Console.WriteLine(
                    Envelope.Error(
                        "manifest.conflict",
                        "published action version is immutable"));

                return 1;
            }

            Console.WriteLine(
                Envelope.Error(
                    "action.publish_failed",
                    "failed to publish action"));

            return 1;
        }
        catch
        {
            await tx.RollbackAsync();

            Console.WriteLine(
                Envelope.Error(
                    "action.publish_failed",
                    "failed to publish action"));

            return 1;
        }
    }

    public static async Task<int> ListAsync()
    {
        await using var conn = new NpgsqlConnection(Database.ConnStr());
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                module,
                action,
                version,
                enabled,
                is_default
            FROM autocheck.action_definitions
            ORDER BY module, action, version
            """,
            conn);

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

        Console.WriteLine(
            Envelope.Ok(
                new
                {
                    resource = "action",
                    operation = "listed",
                    items
                }));

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
                    if (!int.TryParse(rest[++i], out var parsedVersion) ||
                        parsedVersion < 1)
                    {
                        Console.WriteLine(
                            Envelope.Error(
                                "action.invalid_arguments",
                                "--version must be a positive integer"));

                        return 1;
                    }

                    version = parsedVersion;
                    break;

                case "--replacement-version" when i + 1 < rest.Length:
                    if (!int.TryParse(rest[++i], out var parsedReplacement) ||
                        parsedReplacement < 1)
                    {
                        Console.WriteLine(
                            Envelope.Error(
                                "action.invalid_arguments",
                                "--replacement-version must be a positive integer"));

                        return 1;
                    }

                    replacement = parsedReplacement;
                    break;

                default:
                    if (route is null)
                    {
                        route = rest[i];
                    }
                    else
                    {
                        Console.WriteLine(
                            Envelope.Error(
                                "action.invalid_arguments",
                                "unexpected argument"));

                        return 1;
                    }

                    break;
            }
        }

        if (route is null || !route.Contains('.'))
        {
            Console.WriteLine(
                Envelope.Error(
                    "action.invalid_arguments",
                    "route key 'module.action' is required"));

            return 1;
        }

        if (version is null)
        {
            Console.WriteLine(
                Envelope.Error(
                    "action.invalid_arguments",
                    "--version is required"));

            return 1;
        }

        var dot = route.IndexOf('.');

        var module = route[..dot];
        var action = route[(dot + 1)..];

        await using var conn = new NpgsqlConnection(Database.ConnStr());
        await conn.OpenAsync();

        if (op == "activate")
        {
            return await ActivateAsync(
                conn,
                module,
                action,
                version.Value);
        }

        return await DisableAsync(
            conn,
            module,
            action,
            version.Value,
            replacement);
    }

    private static async Task<int> ActivateAsync(
        NpgsqlConnection conn,
        string module,
        string action,
        int version)
    {
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            /*
             * Проверяем, что версия существует и enabled.
             */
            await using var guard = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM autocheck.action_definitions
                WHERE module = @m
                  AND action = @a
                  AND version = @v
                  AND enabled = true
                """,
                conn,
                tx);

            guard.Parameters.AddWithValue("m", module);
            guard.Parameters.AddWithValue("a", action);
            guard.Parameters.AddWithValue("v", version);

            var found = (long)(await guard.ExecuteScalarAsync())!;

            if (found == 0)
            {
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "action.not_found",
                        $"enabled action {module}.{action} version {version} not found"));

                return 1;
            }

            /*
             * Внутри одной transaction назначаем ровно одну default.
             */
            await using var upd = new NpgsqlCommand(
                """
                UPDATE autocheck.action_definitions
                SET is_default = (version = @v)
                WHERE module = @m
                  AND action = @a
                  AND enabled = true
                """,
                conn,
                tx);

            upd.Parameters.AddWithValue("m", module);
            upd.Parameters.AddWithValue("a", action);
            upd.Parameters.AddWithValue("v", version);

            await upd.ExecuteNonQueryAsync();

            await tx.CommitAsync();

            Console.WriteLine(
                Envelope.Ok(
                    new
                    {
                        resource = "action",
                        operation = "activated",
                        key = $"{module}.{action}",
                        version
                    }));

            return 0;
        }
        catch (PostgresException)
        {
            await tx.RollbackAsync();

            Console.WriteLine(
                Envelope.Error(
                    "action.activate_failed",
                    "failed to activate action"));

            return 1;
        }
        catch
        {
            await tx.RollbackAsync();

            Console.WriteLine(
                Envelope.Error(
                    "action.activate_failed",
                    "failed to activate action"));

            return 1;
        }
    }

    private static async Task<int> DisableAsync(
        NpgsqlConnection conn,
        string module,
        string action,
        int version,
        int? replacement)
    {
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            /*
             * Если отключаем default, replacement обязателен.
             * Иначе маршрут останется без default.
             */
            await using var currentCmd = new NpgsqlCommand(
                """
                SELECT enabled, is_default
                FROM autocheck.action_definitions
                WHERE module = @m
                  AND action = @a
                  AND version = @v
                """,
                conn,
                tx);

            currentCmd.Parameters.AddWithValue("m", module);
            currentCmd.Parameters.AddWithValue("a", action);
            currentCmd.Parameters.AddWithValue("v", version);

            await using var currentReader =
                await currentCmd.ExecuteReaderAsync();

            if (!await currentReader.ReadAsync())
            {
                await currentReader.DisposeAsync();
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "action.not_found",
                        $"action {module}.{action} version {version} not found"));

                return 1;
            }

            var enabled = currentReader.GetBoolean(0);
            var isDefault = currentReader.GetBoolean(1);

            await currentReader.DisposeAsync();

            if (!enabled)
            {
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "action.invalid_state",
                        $"action {module}.{action} version {version} is already disabled"));

                return 1;
            }

            if (isDefault && replacement is null)
            {
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "action.invalid_arguments",
                        "--replacement-version is required when disabling the default version"));

                return 1;
            }

            if (replacement == version)
            {
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "action.invalid_arguments",
                        "replacement version must differ from disabled version"));

                return 1;
            }

            /*
             * Если replacement указан, он должен существовать,
             * быть enabled и относиться к тому же route.
             */
            if (replacement is not null)
            {
                await using var replacementCmd = new NpgsqlCommand(
                    """
                    SELECT enabled
                    FROM autocheck.action_definitions
                    WHERE module = @m
                      AND action = @a
                      AND version = @v
                    """,
                    conn,
                    tx);

                replacementCmd.Parameters.AddWithValue("m", module);
                replacementCmd.Parameters.AddWithValue("a", action);
                replacementCmd.Parameters.AddWithValue("v", replacement.Value);

                var replacementEnabled =
                    await replacementCmd.ExecuteScalarAsync();

                if (replacementEnabled is null)
                {
                    await tx.RollbackAsync();

                    Console.WriteLine(
                        Envelope.Error(
                            "action.not_found",
                            $"replacement version {replacement} not found"));

                    return 1;
                }

                if (replacementEnabled is not bool || !(bool)replacementEnabled)
                {
                    await tx.RollbackAsync();

                    Console.WriteLine(
                        Envelope.Error(
                            "action.invalid_state",
                            $"replacement version {replacement} is disabled"));

                    return 1;
                }
            }

            /*
             * Сначала отключаем текущую версию.
             */
            await using var dis = new NpgsqlCommand(
                """
                UPDATE autocheck.action_definitions
                SET enabled = false,
                    is_default = false
                WHERE module = @m
                  AND action = @a
                  AND version = @v
                """,
                conn,
                tx);

            dis.Parameters.AddWithValue("m", module);
            dis.Parameters.AddWithValue("a", action);
            dis.Parameters.AddWithValue("v", version);

            var affected = await dis.ExecuteNonQueryAsync();

            if (affected == 0)
            {
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "action.not_found",
                        $"action {module}.{action} version {version} not found"));

                return 1;
            }

            /*
             * Назначаем replacement новой default.
             */
            if (replacement is not null)
            {
                await using var rep = new NpgsqlCommand(
                    """
                    UPDATE autocheck.action_definitions
                    SET is_default = true
                    WHERE module = @m
                      AND action = @a
                      AND version = @r
                      AND enabled = true
                    """,
                    conn,
                    tx);

                rep.Parameters.AddWithValue("m", module);
                rep.Parameters.AddWithValue("a", action);
                rep.Parameters.AddWithValue("r", replacement.Value);

                var repAffected =
                    await rep.ExecuteNonQueryAsync();

                if (repAffected == 0)
                {
                    await tx.RollbackAsync();

                    Console.WriteLine(
                        Envelope.Error(
                            "action.not_found",
                            $"enabled replacement version {replacement} not found"));

                    return 1;
                }
            }

            await tx.CommitAsync();

            Console.WriteLine(
                Envelope.Ok(
                    new
                    {
                        resource = "action",
                        operation = "disabled",
                        key = $"{module}.{action}",
                        version,
                        replacement
                    }));

            return 0;
        }
        catch (PostgresException)
        {
            await tx.RollbackAsync();

            Console.WriteLine(
                Envelope.Error(
                    "action.disable_failed",
                    "failed to disable action"));

            return 1;
        }
        catch
        {
            await tx.RollbackAsync();

            Console.WriteLine(
                Envelope.Error(
                    "action.disable_failed",
                    "failed to disable action"));

            return 1;
        }
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