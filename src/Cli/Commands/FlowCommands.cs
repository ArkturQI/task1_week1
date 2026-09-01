using System.Text;
using System.Text.Json;
using Cli.Models;
using Cli.Services;
using Npgsql;

namespace Cli.Commands;

internal static class FlowCommands
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false
    };

    public static async Task<int> ValidateAsync(string? file)
    {
        if (!WorkflowMapParser.TryParse(file, out var map, out var parseError))
        {
            Console.WriteLine(
                Envelope.Error(
                    "flow.invalid_map",
                    parseError!));

            return 1;
        }

        var errors = await ValidateMapAsync(map!);

        if (errors.Count > 0)
        {
            Console.WriteLine(
                Envelope.Error(
                    "flow.invalid_map",
                    string.Join("; ", errors)));

            return 1;
        }

        Console.WriteLine(
            Envelope.Ok(
                new
                {
                    resource = "flow",
                    operation = "validated",
                    flowName = map!.FlowName,
                    flowVersion = map.Version
                }));

        return 0;
    }

    public static async Task<int> PublishAsync(string? file)
    {
        if (!WorkflowMapParser.TryParse(file, out var map, out var parseError))
        {
            Console.WriteLine(
                Envelope.Error(
                    "flow.invalid_map",
                    parseError!));

            return 1;
        }

        var validationErrors =
            await ValidateMapAsync(map!);

        if (validationErrors.Count > 0)
        {
            Console.WriteLine(
                Envelope.Error(
                    "flow.invalid_map",
                    string.Join("; ", validationErrors)));

            return 1;
        }

        await using var conn =
            new NpgsqlConnection(Database.ConnStr());

        await conn.OpenAsync();

        await using var tx =
            await conn.BeginTransactionAsync();

        try
        {
            var canonical =
                Canonicalize(map!);

            var canonicalJson =
                JsonSerializer.Serialize(
                    canonical,
                    CanonicalJsonOptions);

            var digest =
                Database.Sha256Hex(canonicalJson);

            Guid flowId;

            await using (
                var findFlow = new NpgsqlCommand(
                    """
                    SELECT flow_id
                    FROM workflow.flow_definitions
                    WHERE flow_name = @name
                    """,
                    conn,
                    tx))
            {
                findFlow.Parameters.AddWithValue(
                    "name",
                    map!.FlowName);

                var existing =
                    await findFlow.ExecuteScalarAsync();

                if (existing is Guid guid)
                {
                    flowId = guid;
                }
                else
                {
                    await using var insertFlow =
                        new NpgsqlCommand(
                            """
                            INSERT INTO workflow.flow_definitions(flow_name)
                            VALUES (@name)
                            RETURNING flow_id
                            """,
                            conn,
                            tx);

                    insertFlow.Parameters.AddWithValue(
                        "name",
                        map.FlowName);

                    flowId =
                        (Guid)(await insertFlow.ExecuteScalarAsync())!;
                }
            }

            await using (
                var existingVersion =
                    new NpgsqlCommand(
                        """
                        SELECT flow_version_id, map::text
                        FROM workflow.flow_versions
                        WHERE flow_name = @name
                          AND flow_version = @version
                        """,
                        conn,
                        tx))
            {
                existingVersion.Parameters.AddWithValue(
                    "name",
                    map.FlowName);

                existingVersion.Parameters.AddWithValue(
                    "version",
                    map.Version);

                await using var reader =
                    await existingVersion.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    var existingId =
                        reader.GetGuid(0);

                    var existingMap =
                        reader.GetString(1);

                    await reader.DisposeAsync();

                    if (!string.Equals(
                            existingMap,
                            canonicalJson,
                            StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync();

                        Console.WriteLine(
                            Envelope.Error(
                                "flow.conflict",
                                "published flow version is immutable"));

                        return 1;
                    }

                    await tx.RollbackAsync();

                    Console.WriteLine(
                        Envelope.Ok(
                            new
                            {
                                resource = "flow",
                                operation = "published",
                                flowName = map.FlowName,
                                flowVersion = map.Version
                            }));

                    return 0;
                }
            }

            Guid flowVersionId;

            await using (
                var insertVersion =
                    new NpgsqlCommand(
                        """
                        INSERT INTO workflow.flow_versions(
                            flow_id,
                            flow_name,
                            flow_version,
                            status,
                            is_active,
                            map
                        )
                        VALUES (
                            @flowId,
                            @flowName,
                            @version,
                            'PUBLISHED',
                            false,
                            @map::jsonb
                        )
                        RETURNING flow_version_id
                        """,
                        conn,
                        tx))
            {
                insertVersion.Parameters.AddWithValue(
                    "flowId",
                    flowId);

                insertVersion.Parameters.AddWithValue(
                    "flowName",
                    map!.FlowName);

                insertVersion.Parameters.AddWithValue(
                    "version",
                    map.Version);

                insertVersion.Parameters.AddWithValue(
                    "map",
                    canonicalJson);

                flowVersionId =
                    (Guid)(await insertVersion.ExecuteScalarAsync())!;
            }

            var stepIds =
                new Dictionary<string, Guid>(
                    StringComparer.Ordinal);

            foreach (var step in map!.Steps)
            {
                await using var insertStep =
                    new NpgsqlCommand(
                        """
                        INSERT INTO workflow.step_definitions(
                            flow_version_id,
                            step_key,
                            step_type,
                            step_config
                        )
                        VALUES (
                            @flowVersionId,
                            @key,
                            @type,
                            @config::jsonb
                        )
                        RETURNING step_definition_id
                        """,
                        conn,
                        tx);

                insertStep.Parameters.AddWithValue(
                    "flowVersionId",
                    flowVersionId);

                insertStep.Parameters.AddWithValue(
                    "key",
                    step.Key);

                insertStep.Parameters.AddWithValue(
                    "type",
                    step.Type);

                insertStep.Parameters.AddWithValue(
                    "config",
                    step.Raw.ValueKind == JsonValueKind.Undefined
                        ? "{}"
                        : step.Raw.GetRawText());

                var stepId =
                    (Guid)(await insertStep.ExecuteScalarAsync())!;

                stepIds[step.Key] = stepId;

                if (step.Type == "automatic")
                {
                    var task = step.Task!;

                    var retryJson =
                        JsonSerializer.Serialize(
                            task.Retry,
                            CanonicalJsonOptions);

                    var mappingJson =
                        JsonSerializer.Serialize(
                            task.InputMapping,
                            CanonicalJsonOptions);

                    var constantsJson =
                        JsonSerializer.Serialize(
                            task.InputConstants,
                            CanonicalJsonOptions);

                    await using var insertTask =
                        new NpgsqlCommand(
                            """
                            INSERT INTO workflow.task_definitions(
                                step_definition_id,
                                service,
                                module,
                                action,
                                action_version,
                                required_policy,
                                timeout_ms,
                                retry_max_attempts,
                                retry_delays_ms,
                                input_mapping,
                                input_constants
                            )
                            VALUES (
                                @stepId,
                                @service,
                                @module,
                                @action,
                                @actionVersion,
                                @policy::jsonb,
                                @timeout,
                                @maxAttempts,
                                @delays::jsonb,
                                @mapping::jsonb,
                                @constants::jsonb
                            )
                            """,
                            conn,
                            tx);

                    insertTask.Parameters.AddWithValue(
                        "stepId",
                        stepId);

                    insertTask.Parameters.AddWithValue(
                        "service",
                        task.Service);

                    insertTask.Parameters.AddWithValue(
                        "module",
                        task.Module);

                    insertTask.Parameters.AddWithValue(
                        "action",
                        task.Action);

                    insertTask.Parameters.AddWithValue(
                        "actionVersion",
                        task.ActionVersion);

                    insertTask.Parameters.AddWithValue(
                        "policy",
                        JsonSerializer.Serialize(
                            task.RequiredPolicy));

                    insertTask.Parameters.AddWithValue(
                        "timeout",
                        task.TimeoutMs);

                    insertTask.Parameters.AddWithValue(
                        "maxAttempts",
                        task.Retry.MaxAttempts);

                    insertTask.Parameters.AddWithValue(
                        "delays",
                        retryJson);

                    insertTask.Parameters.AddWithValue(
                        "mapping",
                        mappingJson);

                    insertTask.Parameters.AddWithValue(
                        "constants",
                        constantsJson);

                    await insertTask.ExecuteNonQueryAsync();
                }
            }

            foreach (var transition in map.Transitions)
            {
                await using var insertTransition =
                    new NpgsqlCommand(
                        """
                        INSERT INTO workflow.transition_definitions(
                            flow_version_id,
                            from_step_key,
                            outcome,
                            to_step_key
                        )
                        VALUES (
                            @flowVersionId,
                            @from,
                            @outcome,
                            @to
                        )
                        """,
                        conn,
                        tx);

                insertTransition.Parameters.AddWithValue(
                    "flowVersionId",
                    flowVersionId);

                insertTransition.Parameters.AddWithValue(
                    "from",
                    transition.From);

                insertTransition.Parameters.AddWithValue(
                    "outcome",
                    transition.Outcome);

                insertTransition.Parameters.AddWithValue(
                    "to",
                    transition.To);

                await insertTransition.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();

            Console.WriteLine(
                Envelope.Ok(
                    new
                    {
                        resource = "flow",
                        operation = "published",
                        flowName = map.FlowName,
                        flowVersion = map.Version
                    }));

            return 0;
        }
        catch (PostgresException ex)
        {
            await tx.RollbackAsync();

            if (ex.SqlState == "23505")
            {
                Console.WriteLine(
                    Envelope.Error(
                        "flow.conflict",
                        "published flow version is immutable"));

                return 1;
            }

            Console.WriteLine(
                Envelope.Error(
                    "flow.publish_failed",
                    "failed to publish workflow"));

            return 1;
        }
        catch
        {
            await tx.RollbackAsync();

            Console.WriteLine(
                Envelope.Error(
                    "flow.publish_failed",
                    "failed to publish workflow"));

            return 1;
        }
    }

    public static async Task<int> ListAsync()
    {
        await using var conn =
            new NpgsqlConnection(Database.ConnStr());

        await conn.OpenAsync();

        await using var cmd =
            new NpgsqlCommand(
                """
                SELECT
                    flow_name,
                    flow_version,
                    status,
                    is_active,
                    published_at
                FROM workflow.flow_versions
                ORDER BY flow_name, flow_version
                """,
                conn);

        await using var reader =
            await cmd.ExecuteReaderAsync();

        var items =
            new List<object>();

        while (await reader.ReadAsync())
        {
            items.Add(
                new
                {
                    flowName = reader.GetString(0),
                    flowVersion = reader.GetInt32(1),
                    status = reader.GetString(2),
                    isActive = reader.GetBoolean(3),
                    publishedAt = reader.GetDateTime(4)
                });
        }

        Console.WriteLine(
            Envelope.Ok(
                new
                {
                    resource = "flow",
                    operation = "listed",
                    items
                }));

        return 0;
    }

    // НОВЫЙ ActivateAsync (полностью заменён)
    public static async Task<int> ActivateAsync(
        string[] args)
    {
        if (args.Length != 3 ||
            !string.Equals(
                args[1],
                "--version",
                StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                args[2],
                out var version) ||
            version < 1)
        {
            Console.WriteLine(
                Envelope.Error(
                    "flow.invalid_arguments",
                    "usage: flow activate <flow> --version <version>"));

            return 1;
        }

        var flowName = args[0];

        await using var conn =
            new NpgsqlConnection(Database.ConnStr());

        await conn.OpenAsync();

        await using var tx =
            await conn.BeginTransactionAsync();

        try
        {
            await using var findCommand =
                new NpgsqlCommand(
                    """
                    SELECT flow_version_id
                    FROM workflow.flow_versions
                    WHERE flow_name = @flow
                      AND flow_version = @version
                      AND status = 'PUBLISHED'
                    FOR UPDATE
                    """,
                    conn,
                    tx);

            findCommand.Parameters.AddWithValue(
                "flow",
                flowName);

            findCommand.Parameters.AddWithValue(
                "version",
                version);

            var flowVersionId =
                await findCommand.ExecuteScalarAsync();

            if (flowVersionId is not Guid)
            {
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "flow.not_found",
                        "published flow version not found"));

                return 1;
            }

            /*
             * В пределах одной transaction сначала снимаем active
             * со старой версии, затем активируем новую.
             *
             * Благодаря partial unique index в БД одновременно
             * активной может быть только одна версия конкретного flow.
             */
            await using var deactivateCommand =
                new NpgsqlCommand(
                    """
                    UPDATE workflow.flow_versions
                    SET is_active = false
                    WHERE flow_name = @flow
                      AND is_active = true
                      AND flow_version <> @version
                    """,
                    conn,
                    tx);

            deactivateCommand.Parameters.AddWithValue(
                "flow",
                flowName);

            deactivateCommand.Parameters.AddWithValue(
                "version",
                version);

            await deactivateCommand.ExecuteNonQueryAsync();

            await using var activateCommand =
                new NpgsqlCommand(
                    """
                    UPDATE workflow.flow_versions
                    SET is_active = true
                    WHERE flow_name = @flow
                      AND flow_version = @version
                      AND status = 'PUBLISHED'
                    """,
                    conn,
                    tx);

            activateCommand.Parameters.AddWithValue(
                "flow",
                flowName);

            activateCommand.Parameters.AddWithValue(
                "version",
                version);

            var affected =
                await activateCommand.ExecuteNonQueryAsync();

            if (affected != 1)
            {
                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Error(
                        "flow.activate_failed",
                        "workflow version could not be activated"));

                return 1;
            }

            await tx.CommitAsync();

            Console.WriteLine(
                Envelope.Ok(
                    new
                    {
                        resource = "flow",
                        operation = "activated",
                        flowName,
                        flowVersion = version
                    }));

            return 0;
        }
        catch (PostgresException)
        {
            await tx.RollbackAsync();

            Console.WriteLine(
                Envelope.Error(
                    "flow.activate_failed",
                    "failed to activate workflow"));

            return 1;
        }
        catch
        {
            await tx.RollbackAsync();

            Console.WriteLine(
                Envelope.Error(
                    "flow.activate_failed",
                    "failed to activate workflow"));

            return 1;
        }
    }

    private static async Task<List<string>> ValidateMapAsync(
        WorkflowMap map)
    {
        var errors =
            new List<string>();

        if (map.ContractVersion != "course-1")
            errors.Add("contract_version must be course-1");

        if (string.IsNullOrWhiteSpace(map.FlowName))
            errors.Add("flow_name is required");

        if (map.Version < 1)
            errors.Add("version must be positive");

        if (map.Steps.Count < 2)
            errors.Add("at least two steps are required");

        if (string.IsNullOrWhiteSpace(map.StartStep))
        {
            errors.Add("start_step is required");
        }

        var duplicateKeys =
            map.Steps
                .GroupBy(s => s.Key, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

        foreach (var duplicate in duplicateKeys)
            errors.Add($"duplicate step key: {duplicate}");

        var stepsByKey =
            map.Steps.ToDictionary(
                s => s.Key,
                StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(map.StartStep) &&
            !stepsByKey.ContainsKey(map.StartStep))
        {
            errors.Add("start_step does not exist");
        }

        foreach (var step in map.Steps)
        {
            switch (step.Type)
            {
                case "automatic":
                    if (step.Task is null)
                        errors.Add(
                            $"automatic step {step.Key} requires task");

                    break;

                case "wait_signal":
                    if (string.IsNullOrWhiteSpace(step.SignalType))
                        errors.Add(
                            $"wait_signal step {step.Key} requires signal_type");

                    if (string.IsNullOrWhiteSpace(step.Outcome))
                        errors.Add(
                            $"wait_signal step {step.Key} requires outcome");

                    break;

                case "manual":
                    if (step.AllowedOutcomes is null ||
                        step.AllowedOutcomes.Count == 0)
                    {
                        errors.Add(
                            $"manual step {step.Key} requires allowed_outcomes");
                    }

                    break;

                case "end":
                    if (string.IsNullOrWhiteSpace(step.Outcome))
                        errors.Add(
                            $"end step {step.Key} requires outcome");

                    break;

                default:
                    errors.Add(
                        $"unknown step type: {step.Type}");

                    break;
            }
        }

        foreach (var transition in map.Transitions)
        {
            if (!stepsByKey.ContainsKey(transition.From))
                errors.Add(
                    $"transition source does not exist: {transition.From}");

            if (!stepsByKey.ContainsKey(transition.To))
                errors.Add(
                    $"transition target does not exist: {transition.To}");

            if (string.IsNullOrWhiteSpace(transition.Outcome))
                errors.Add(
                    "transition outcome is required");
        }

        var endSteps =
            map.Steps
                .Where(s => s.Type == "end")
                .ToList();

        if (endSteps.Count == 0)
            errors.Add("workflow must contain at least one end step");

        foreach (var transition in map.Transitions)
        {
            if (stepsByKey.TryGetValue(
                    transition.From,
                    out var fromStep) &&
                fromStep.Type == "end")
            {
                errors.Add(
                    $"end step cannot have outgoing transition: {fromStep.Key}");
            }
        }

        errors.AddRange(
            ValidateTransitionCoverage(
                map,
                stepsByKey));

        errors.AddRange(
            ValidateGraph(
                map,
                stepsByKey));

        errors.AddRange(
            ValidateTaskLocalRules(map));

        var databaseErrors =
            await ValidateActionsAgainstDatabaseAsync(map);

        errors.AddRange(databaseErrors);

        return errors;
    }

    // ЗАМЕНЁННЫЙ ValidateTransitionCoverage
    private static List<string> ValidateTransitionCoverage(
        WorkflowMap map,
        Dictionary<string, WorkflowStep> steps)
    {
        var errors = new List<string>();

        var grouped =
            map.Transitions
                .GroupBy(t => (t.From, t.Outcome));

        foreach (var group in grouped)
        {
            if (group.Count() > 1)
            {
                errors.Add(
                    $"duplicate transition for {group.Key.From}:{group.Key.Outcome}");
            }
        }

        foreach (var step in map.Steps)
        {
            if (step.Type == "automatic" &&
                step.Task is not null)
            {
                // Для automatic step полный набор outcomes
                // проверяется отдельно через action catalog.
                continue;
            }

            List<string> required;

            if (step.Type == "wait_signal" &&
                !string.IsNullOrWhiteSpace(step.Outcome))
            {
                required = new List<string>
                {
                    step.Outcome!
                };
            }
            else if (step.Type == "manual")
            {
                required =
                    step.AllowedOutcomes is not null
                        ? new List<string>(step.AllowedOutcomes)
                        : new List<string>();
            }
            else
            {
                required = new List<string>();
            }

            foreach (var outcome in required)
            {
                var count =
                    map.Transitions.Count(
                        t => t.From == step.Key &&
                             t.Outcome == outcome);

                if (count != 1)
                {
                    errors.Add(
                        $"step {step.Key} requires exactly one transition for outcome {outcome}");
                }
            }
        }

        return errors;
    }

    // НОВАЯ ВЕРСИЯ ValidateTaskLocalRules
    private static List<string> ValidateTaskLocalRules(
        WorkflowMap map)
    {
        var errors =
            new List<string>();

        foreach (var step in map.Steps.Where(
                     s => s.Type == "automatic" &&
                          s.Task is not null))
        {
            var task = step.Task!;

            if (!string.Equals(
                    task.Service,
                    "postgres",
                    StringComparison.Ordinal))
            {
                errors.Add(
                    $"unsupported task service: {task.Service}");
            }

            if (task.TimeoutMs < 1 ||
                task.TimeoutMs > 30000)
            {
                errors.Add(
                    $"invalid timeout_ms for step {step.Key}");
            }

            if (task.Retry.MaxAttempts < 1 ||
                task.Retry.MaxAttempts > 10)
            {
                errors.Add(
                    $"invalid retry.max_attempts for step {step.Key}");
            }

            if (task.Retry.DelaysMs.Count !=
                Math.Max(
                    0,
                    task.Retry.MaxAttempts - 1))
            {
                errors.Add(
                    $"retry.delays_ms count must equal max_attempts - 1 for step {step.Key}");
            }

            if (task.Retry.DelaysMs.Any(
                    delay => delay < 0 ||
                             delay > 30000))
            {
                errors.Add(
                    $"invalid retry delay for step {step.Key}");
            }

            /*
             * input_mapping:
             *
             *   target payload pointer -> source process-data pointer
             *
             * Например:
             *
             *   "/value" -> "/subject"
             *
             * Оба значения являются RFC 6901 JSON Pointers.
             */
            foreach (var pair in task.InputMapping)
            {
                if (!IsValidJsonPointer(pair.Key))
                {
                    errors.Add(
                        $"invalid target JSON Pointer: {pair.Key}");
                }

                if (!IsValidJsonPointer(pair.Value))
                {
                    errors.Add(
                        $"invalid source JSON Pointer: {pair.Value}");
                }
            }

            /*
             * input_constants:
             *
             * Это обычный JSON object.
             *
             * Ключи НЕ являются JSON Pointers.
             *
             * При построении payload каждый key становится
             * top-level property:
             *
             *   "marker": "value"
             *
             * эквивалентно target pointer:
             *
             *   "/marker"
             *
             * Поэтому для проверки overlap переводим key
             * в соответствующий top-level pointer.
             */
            foreach (var key in task.InputConstants.Keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    errors.Add(
                        $"input_constants contains an empty key in step {step.Key}");
                }
            }

            /*
             * Проверяем пересечения target mappings
             * между собой и с input_constants.
             *
             * /a пересекается с /a/b
             * /a не пересекается с /ab
             */
            var targetPointers =
                new List<(string Pointer, string Source)>();

            foreach (var pair in task.InputMapping)
            {
                targetPointers.Add(
                    (
                        pair.Key,
                        "input_mapping"
                    ));
            }

            foreach (var constantKey in task.InputConstants.Keys)
            {
                /*
                 * input_constants является top-level JSON object,
                 * поэтому key преобразуется в /key.
                 *
                 * Для RFC 6901 экранируем ~ и /.
                 */
                var escapedKey =
                    constantKey
                        .Replace("~", "~0")
                        .Replace("/", "~1");

                targetPointers.Add(
                    (
                        "/" + escapedKey,
                        "input_constants"
                    ));
            }

            for (var i = 0;
                 i < targetPointers.Count;
                 i++)
            {
                for (var j = i + 1;
                     j < targetPointers.Count;
                     j++)
                {
                    if (JsonPointersOverlap(
                            targetPointers[i].Pointer,
                            targetPointers[j].Pointer))
                    {
                        errors.Add(
                            $"overlapping target mappings: " +
                            $"{targetPointers[i].Pointer} " +
                            $"({targetPointers[i].Source}) and " +
                            $"{targetPointers[j].Pointer} " +
                            $"({targetPointers[j].Source})");
                    }
                }
            }
        }

        return errors;
    }

    private static List<string> ValidateGraph(
        WorkflowMap map,
        Dictionary<string, WorkflowStep> steps)
    {
        var errors =
            new List<string>();

        if (!steps.ContainsKey(map.StartStep))
            return errors;

        var adjacency =
            map.Transitions
                .GroupBy(t => t.From)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.To).ToList(),
                    StringComparer.Ordinal);

        var reachable =
            new HashSet<string>(
                StringComparer.Ordinal);

        var queue =
            new Queue<string>();

        queue.Enqueue(map.StartStep);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!reachable.Add(current))
                continue;

            if (!adjacency.TryGetValue(current, out var next))
                continue;

            foreach (var target in next)
                queue.Enqueue(target);
        }

        foreach (var step in map.Steps)
        {
            if (!reachable.Contains(step.Key))
            {
                errors.Add(
                    $"unreachable step: {step.Key}");
            }
        }

        if (!map.Steps.Any(
                s => reachable.Contains(s.Key) &&
                     s.Type == "end"))
        {
            errors.Add(
                "no reachable end step");
        }

        var visiting =
            new HashSet<string>(
                StringComparer.Ordinal);

        var visited =
            new HashSet<string>(
                StringComparer.Ordinal);

        bool Dfs(string node)
        {
            if (visiting.Contains(node))
                return true;

            if (!visited.Add(node))
                return false;

            visiting.Add(node);

            if (adjacency.TryGetValue(node, out var next))
            {
                foreach (var target in next)
                {
                    if (Dfs(target))
                        return true;
                }
            }

            visiting.Remove(node);
            return false;
        }

        if (Dfs(map.StartStep))
            errors.Add("workflow graph contains a cycle");

        foreach (var step in map.Steps)
        {
            if (step.Type != "end" &&
                !map.Transitions.Any(t => t.From == step.Key))
            {
                errors.Add(
                    $"non-end step has no outgoing transition: {step.Key}");
            }
        }

        return errors;
    }

    private static async Task<List<string>>
        ValidateActionsAgainstDatabaseAsync(
            WorkflowMap map)
    {
        var errors =
            new List<string>();

        await using var conn =
            new NpgsqlConnection(Database.ConnStr());

        await conn.OpenAsync();

        foreach (var step in map.Steps.Where(
                     s => s.Type == "automatic" &&
                          s.Task is not null))
        {
            var task = step.Task!;

            await using var cmd =
                new NpgsqlCommand(
                    """
                    SELECT manifest, enabled
                    FROM autocheck.action_definitions
                    WHERE module = @module
                      AND action = @action
                      AND version = @version
                    """,
                    conn);

            cmd.Parameters.AddWithValue(
                "module",
                task.Module);

            cmd.Parameters.AddWithValue(
                "action",
                task.Action);

            cmd.Parameters.AddWithValue(
                "version",
                task.ActionVersion);

            await using var reader =
                await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                await reader.DisposeAsync();

                errors.Add(
                    $"unknown action version: {task.Module}.{task.Action} v{task.ActionVersion}");

                continue;
            }

            var manifest =
                reader.GetFieldValue<JsonDocument>(0);

            var enabled =
                reader.GetBoolean(1);

            await reader.DisposeAsync();

            if (!enabled)
            {
                errors.Add(
                    $"disabled action version: {task.Module}.{task.Action} v{task.ActionVersion}");
            }

            var root = manifest.RootElement;

            var outcomes =
                root.TryGetProperty(
                    "outcomes",
                    out var outcomeElement)
                    ? outcomeElement
                        .EnumerateArray()
                        .Select(x => x.GetString() ?? "")
                        .ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(
                        StringComparer.Ordinal);

            var policy =
                root.TryGetProperty(
                    "required_policy",
                    out var policyElement)
                    ? policyElement
                        .EnumerateArray()
                        .Select(x => x.GetString() ?? "")
                        .ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(
                        StringComparer.Ordinal);

            var taskPolicy =
                task.RequiredPolicy
                    .ToHashSet(StringComparer.Ordinal);

            if (!policy.SetEquals(taskPolicy))
            {
                errors.Add(
                    $"policy mismatch for {task.Module}.{task.Action} v{task.ActionVersion}");
            }

            var actualTransitions =
                map.Transitions
                    .Where(t => t.From == step.Key)
                    .Select(t => t.Outcome)
                    .ToHashSet(StringComparer.Ordinal);

            if (!outcomes.SetEquals(actualTransitions))
            {
                errors.Add(
                    $"action outcomes are not covered exactly once for step {step.Key}");
            }

            // Current week-2 worker server-side capability.
            var workerScopes =
                new HashSet<string>(
                    new[]
                    {
                        "workflow:execute",
                        "workflow:read"
                    },
                    StringComparer.Ordinal);

            if (!taskPolicy.IsSubsetOf(workerScopes))
            {
                errors.Add(
                    $"worker policy is not sufficient for step {step.Key}");
            }
        }

        return errors;
    }

    private static bool IsValidJsonPointer(
        string value)
    {
        return value == "/" ||
               (value.StartsWith('/') &&
                value.Split('/').Skip(1).All(IsValidPointerToken));
    }

    private static bool IsValidPointerToken(
        string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '~')
            {
                if (i + 1 >= value.Length)
                    return false;

                if (value[i + 1] is not ('0' or '1'))
                    return false;

                i++;
            }
        }

        return true;
    }

    private static bool JsonPointersOverlap(
        string left,
        string right)
    {
        if (left == "/" || right == "/")
            return true;

        var a =
            left.Split('/')[1..];

        var b =
            right.Split('/')[1..];

        var min =
            Math.Min(a.Length, b.Length);

        for (var i = 0; i < min; i++)
        {
            var leftToken =
                DecodePointerToken(a[i]);

            var rightToken =
                DecodePointerToken(b[i]);

            if (!string.Equals(
                    leftToken,
                    rightToken,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string DecodePointerToken(
        string token)
    {
        return token
            .Replace("~1", "/")
            .Replace("~0", "~");
    }

    private static object Canonicalize(
        WorkflowMap map)
    {
        return new
        {
            contract_version = map.ContractVersion,
            flow_name = map.FlowName,
            version = map.Version,
            start_step = map.StartStep,
            steps = map.Steps
                .Select(step => new
                {
                    key = step.Key,
                    type = step.Type,
                    task = step.Task is null
                        ? null
                        : new
                        {
                            service = step.Task.Service,
                            module = step.Task.Module,
                            action = step.Task.Action,
                            action_version =
                                step.Task.ActionVersion,
                            required_policy =
                                step.Task.RequiredPolicy
                                    .OrderBy(x => x)
                                    .ToArray(),
                            timeout_ms =
                                step.Task.TimeoutMs,
                            retry = new
                            {
                                max_attempts =
                                    step.Task.Retry.MaxAttempts,
                                delays_ms =
                                    step.Task.Retry.DelaysMs
                            },
                            input_mapping =
                                step.Task.InputMapping
                                    .OrderBy(x => x.Key)
                                    .ToDictionary(
                                        x => x.Key,
                                        x => x.Value),
                            input_constants =
                                step.Task.InputConstants
                                    .OrderBy(x => x.Key)
                                    .ToDictionary(
                                        x => x.Key,
                                        x => x.Value)
                        },
                    signal_type = step.SignalType,
                    outcome = step.Outcome,
                    allowed_outcomes = step.AllowedOutcomes
                })
                .ToArray(),
            transitions = map.Transitions
                .Select(t => new
                {
                    from = t.From,
                    outcome = t.Outcome,
                    to = t.To
                })
                .ToArray()
        };
    }
}