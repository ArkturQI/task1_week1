using System.Text.Json;
using Cli.Models;
using Cli.Services;
using Npgsql;

namespace Cli.Commands;

internal static class FlowRuntimeCommands
{
    public static async Task<int> StartAsync(string[] args)
    {
        if (!TryParseStartArguments(
                args,
                out var flowName,
                out var businessKey,
                out var dataPath,
                out var argumentError))
        {
            return Fail(
                "flow.invalid_arguments",
                argumentError!);
        }

        var (data, dataError) =
            await TryReadJsonObjectAsync(dataPath!);

        if (data is null)
        {
            return Fail(
                "flow.invalid_data",
                dataError!);
        }

        await using var conn =
            new NpgsqlConnection(Database.ConnStr());

        await conn.OpenAsync();

        await using var tx =
            await conn.BeginTransactionAsync();

        try
        {
            await using var versionCommand =
                new NpgsqlCommand(
                    """
                    SELECT
                        fv.flow_id,
                        fv.flow_version_id,
                        fv.flow_version,
                        fv.map
                    FROM workflow.flow_versions fv
                    WHERE fv.flow_name = @flowName
                      AND fv.status = 'PUBLISHED'
                      AND fv.is_active = true
                    LIMIT 1
                    """,
                    conn,
                    tx);

            versionCommand.Parameters.AddWithValue(
                "flowName",
                flowName!);

            await using var versionReader =
                await versionCommand.ExecuteReaderAsync();

            if (!await versionReader.ReadAsync())
            {
                await versionReader.DisposeAsync();
                await tx.RollbackAsync();

                return Fail(
                    "flow.not_active",
                    "no active workflow version");
            }

            var flowId =
                versionReader.GetGuid(0);

            var flowVersionId =
                versionReader.GetGuid(1);

            var flowVersion =
                versionReader.GetInt32(2);

            var mapDocument =
                versionReader.GetFieldValue<JsonDocument>(3);

            await versionReader.DisposeAsync();

            await using var existingProcessCommand =
                new NpgsqlCommand(
                    """
                    SELECT
                        process_id,
                        flow_version,
                        state,
                        data
                    FROM workflow.process_instances
                    WHERE flow_name = @flowName
                      AND business_key = @businessKey
                    LIMIT 1
                    """,
                    conn,
                    tx);

            existingProcessCommand.Parameters.AddWithValue(
                "flowName",
                flowName!);

            existingProcessCommand.Parameters.AddWithValue(
                "businessKey",
                businessKey!);

            await using var existingReader =
                await existingProcessCommand.ExecuteReaderAsync();

            if (await existingReader.ReadAsync())
            {
                var existingProcessId =
                    existingReader.GetGuid(0);

                var existingVersion =
                    existingReader.GetInt32(1);

                var existingState =
                    existingReader.GetString(2);

                var existingData =
                    existingReader.GetFieldValue<JsonDocument>(3);

                await existingReader.DisposeAsync();

                if (!JsonDocumentsEqual(
                        existingData,
                        data))
                {
                    await tx.RollbackAsync();

                    return Fail(
                        "flow.conflict",
                        "business key already belongs to a different process payload");
                }

                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Ok(
                        new
                        {
                            resource = "process",
                            operation = "started",
                            processId =
                                existingProcessId.ToString(),
                            flowName,
                            flowVersion = existingVersion,
                            state = existingState
                        }));

                return 0;
            }

            var processId =
                Guid.NewGuid();

            var startStepKey =
                GetRequiredString(
                    mapDocument.RootElement,
                    "start_step");

            await using var insertProcessCommand =
                new NpgsqlCommand(
                    """
                    INSERT INTO workflow.process_instances(
                        process_id,
                        flow_id,
                        flow_version_id,
                        flow_name,
                        flow_version,
                        business_key,
                        state,
                        current_step_key,
                        data
                    )
                    VALUES (
                        @processId,
                        @flowId,
                        @flowVersionId,
                        @flowName,
                        @flowVersion,
                        @businessKey,
                        @state,
                        @currentStepKey,
                        @data::jsonb
                    )
                    """,
                    conn,
                    tx);

            insertProcessCommand.Parameters.AddWithValue(
                "processId",
                processId);

            insertProcessCommand.Parameters.AddWithValue(
                "flowId",
                flowId);

            insertProcessCommand.Parameters.AddWithValue(
                "flowVersionId",
                flowVersionId);

            insertProcessCommand.Parameters.AddWithValue(
                "flowName",
                flowName!);

            insertProcessCommand.Parameters.AddWithValue(
                "flowVersion",
                flowVersion);

            insertProcessCommand.Parameters.AddWithValue(
                "businessKey",
                businessKey!);

            insertProcessCommand.Parameters.AddWithValue(
                "state",
                "RUNNING");

            insertProcessCommand.Parameters.AddWithValue(
                "currentStepKey",
                startStepKey);

            insertProcessCommand.Parameters.AddWithValue(
                "data",
                data.RootElement.GetRawText());

            await insertProcessCommand.ExecuteNonQueryAsync();

            await using var startStepCommand =
                new NpgsqlCommand(
                    """
                    SELECT
                        sd.step_definition_id,
                        sd.step_key,
                        sd.step_type,
                        sd.step_config
                    FROM workflow.step_definitions sd
                    WHERE sd.flow_version_id = @flowVersionId
                      AND sd.step_key = @stepKey
                    """,
                    conn,
                    tx);

            startStepCommand.Parameters.AddWithValue(
                "flowVersionId",
                flowVersionId);

            startStepCommand.Parameters.AddWithValue(
                "stepKey",
                startStepKey);

            await using var stepReader =
                await startStepCommand.ExecuteReaderAsync();

            if (!await stepReader.ReadAsync())
            {
                await stepReader.DisposeAsync();
                await tx.RollbackAsync();

                return Fail(
                    "flow.invalid_definition",
                    "start step definition not found");
            }

            var stepDefinitionId =
                stepReader.GetGuid(0);

            var stepKey =
                stepReader.GetString(1);

            var stepType =
                stepReader.GetString(2);

            var stepConfig =
                stepReader.GetFieldValue<JsonDocument>(3);

            await stepReader.DisposeAsync();

            var stepInstanceId =
                Guid.NewGuid();

            var stepState =
                stepType switch
                {
                    "automatic" => "READY",
                    "wait_signal" => "WAITING",
                    "manual" => "WAITING",
                    "end" => "COMPLETED",
                    _ => throw new InvalidOperationException(
                        $"unsupported start step type: {stepType}")
                };

            var processState =
                stepType switch
                {
                    "automatic" => "RUNNING",
                    "wait_signal" => "WAITING_SIGNAL",
                    "manual" => "WAITING_MANUAL",
                    "end" => "COMPLETED",
                    _ => throw new InvalidOperationException(
                        $"unsupported start step type: {stepType}")
                };

            await using var insertStepCommand =
                new NpgsqlCommand(
                    """
                    INSERT INTO workflow.step_instances(
                        step_instance_id,
                        process_id,
                        step_key,
                        step_type,
                        state
                    )
                    VALUES (
                        @stepInstanceId,
                        @processId,
                        @stepKey,
                        @stepType,
                        @state
                    )
                    """,
                    conn,
                    tx);

            insertStepCommand.Parameters.AddWithValue(
                "stepInstanceId",
                stepInstanceId);

            insertStepCommand.Parameters.AddWithValue(
                "processId",
                processId);

            insertStepCommand.Parameters.AddWithValue(
                "stepKey",
                stepKey);

            insertStepCommand.Parameters.AddWithValue(
                "stepType",
                stepType.ToUpperInvariant());

            insertStepCommand.Parameters.AddWithValue(
                "state",
                stepState);

            await insertStepCommand.ExecuteNonQueryAsync();

            if (stepType == "automatic")
            {
                await using var taskCommand =
                    new NpgsqlCommand(
                        """
                        SELECT task_definition_id
                        FROM workflow.task_definitions
                        WHERE step_definition_id = @stepDefinitionId
                        """,
                        conn,
                        tx);

                taskCommand.Parameters.AddWithValue(
                    "stepDefinitionId",
                    stepDefinitionId);

                var taskExists =
                    await taskCommand.ExecuteScalarAsync();

                if (taskExists is null)
                {
                    await tx.RollbackAsync();

                    return Fail(
                        "flow.invalid_definition",
                        "automatic start step has no task definition");
                }

                await using var insertJobCommand =
                    new NpgsqlCommand(
                        """
                        INSERT INTO workflow.jobs(
                            job_id,
                            process_id,
                            step_instance_id,
                            execution_id,
                            state,
                            lease_version,
                            attempt_count,
                            failure_count,
                            next_attempt_at
                        )
                        VALUES (
                            @jobId,
                            @processId,
                            @stepInstanceId,
                            @executionId,
                            'READY',
                            0,
                            0,
                            0,
                            clock_timestamp()
                        )
                        """,
                        conn,
                        tx);

                insertJobCommand.Parameters.AddWithValue(
                    "jobId",
                    Guid.NewGuid());

                insertJobCommand.Parameters.AddWithValue(
                    "processId",
                    processId);

                insertJobCommand.Parameters.AddWithValue(
                    "stepInstanceId",
                    stepInstanceId);

                insertJobCommand.Parameters.AddWithValue(
                    "executionId",
                    Guid.NewGuid());

                await insertJobCommand.ExecuteNonQueryAsync();
            }

            if (stepType == "end")
            {
                var outcome =
                    GetOptionalString(
                        stepConfig.RootElement,
                        "outcome");

                await using var completeEndCommand =
                    new NpgsqlCommand(
                        """
                        UPDATE workflow.step_instances
                        SET outcome = @outcome,
                            completed_at = clock_timestamp()
                        WHERE step_instance_id = @stepInstanceId
                        """,
                        conn,
                        tx);

                completeEndCommand.Parameters.AddWithValue(
                    "outcome",
                    (object?)outcome ??
                    DBNull.Value);

                completeEndCommand.Parameters.AddWithValue(
                    "stepInstanceId",
                    stepInstanceId);

                await completeEndCommand.ExecuteNonQueryAsync();
            }

            await using var updateProcessStateCommand =
                new NpgsqlCommand(
                    """
                    UPDATE workflow.process_instances
                    SET state = @state,
                        updated_at = clock_timestamp()
                    WHERE process_id = @processId
                    """,
                    conn,
                    tx);

            updateProcessStateCommand.Parameters.AddWithValue(
                "state",
                processState);

            updateProcessStateCommand.Parameters.AddWithValue(
                "processId",
                processId);

            await updateProcessStateCommand.ExecuteNonQueryAsync();

            await using var eventCommand =
                new NpgsqlCommand(
                    """
                    INSERT INTO workflow.events(
                        event_id,
                        process_id,
                        step_instance_id,
                        event_type,
                        payload
                    )
                    VALUES (
                        gen_random_uuid(),
                        @processId,
                        @stepInstanceId,
                        'ProcessStarted',
                        @payload::jsonb
                    )
                    """,
                    conn,
                    tx);

            eventCommand.Parameters.AddWithValue(
                "processId",
                processId);

            eventCommand.Parameters.AddWithValue(
                "stepInstanceId",
                stepInstanceId);

            eventCommand.Parameters.AddWithValue(
                "payload",
                JsonSerializer.Serialize(
                    new
                    {
                        flowName,
                        flowVersion,
                        startStep = startStepKey
                    }));

            await eventCommand.ExecuteNonQueryAsync();

            await tx.CommitAsync();

            Console.WriteLine(
                Envelope.Ok(
                    new
                    {
                        resource = "process",
                        operation = "started",
                        processId =
                            processId.ToString(),
                        flowName,
                        flowVersion,
                        state = processState
                    }));

            return 0;
        }
        catch (PostgresException ex)
        {
            await tx.RollbackAsync();

            return Fail(
                "flow.start_failed",
                ex.SqlState == "23505"
                    ? "process already exists"
                    : "failed to start workflow process");
        }
        catch
        {
            await tx.RollbackAsync();

            return Fail(
                "flow.start_failed",
                "failed to start workflow process");
        }
        finally
        {
            data.Dispose();
        }
    }

    public static async Task<int> GetAsync(
        string[] args)
    {
        if (args.Length != 1 ||
            !Guid.TryParse(args[0], out var processId))
        {
            return Fail(
                "flow.invalid_arguments",
                "usage: flow get <process-id>");
        }

        await using var conn =
            new NpgsqlConnection(Database.ConnStr());

        await conn.OpenAsync();

        await using var processCommand =
            new NpgsqlCommand(
                """
                SELECT
                    process_id,
                    business_key,
                    flow_name,
                    flow_version,
                    state,
                    current_step_key,
                    created_at,
                    updated_at
                FROM workflow.process_instances
                WHERE process_id = @processId
                """,
                conn);

        processCommand.Parameters.AddWithValue(
            "processId",
            processId);

        await using var reader =
            await processCommand.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Fail(
                "process.not_found",
                "process not found");
        }

        var processIdText =
            reader.GetGuid(0).ToString();

        var businessKey =
            reader.GetString(1);

        var flowName =
            reader.GetString(2);

        var flowVersion =
            reader.GetInt32(3);

        var state =
            reader.GetString(4);

        var currentStepKey =
            reader.IsDBNull(5)
                ? null
                : reader.GetString(5);

        var createdAt =
            reader.GetDateTime(6);

        var updatedAt =
            reader.GetDateTime(7);

        Console.WriteLine(
            Envelope.Ok(
                new
                {
                    resource = "process",
                    processId = processIdText,
                    businessKey,
                    flowName,
                    flowVersion,
                    state,
                    currentStepKey,
                    createdAt,
                    updatedAt
                }));

        return 0;
    }

    public static async Task<int> SignalAsync(
        string[] args)
    {
        if (!TryParseSignalArguments(
                args,
                out var processId,
                out var signalType,
                out var messageId,
                out var payloadPath,
                out var argumentError))
        {
            return Fail(
                "flow.invalid_arguments",
                argumentError!);
        }

        var (payload, payloadError) =
            await TryReadJsonObjectAsync(payloadPath!);

        if (payload is null)
        {
            return Fail(
                "flow.invalid_data",
                payloadError!);
        }

        await using var conn =
            new NpgsqlConnection(Database.ConnStr());

        await conn.OpenAsync();

        await using var tx =
            await conn.BeginTransactionAsync();

        try
        {
            await using var processCommand =
                new NpgsqlCommand(
                    """
                    SELECT
                        process_id,
                        flow_version_id,
                        state
                    FROM workflow.process_instances
                    WHERE process_id = @processId
                    FOR UPDATE
                    """,
                    conn,
                    tx);

            processCommand.Parameters.AddWithValue(
                "processId",
                processId);

            await using var processReader =
                await processCommand.ExecuteReaderAsync();

            if (!await processReader.ReadAsync())
            {
                await processReader.DisposeAsync();
                await tx.RollbackAsync();

                return Fail(
                    "process.not_found",
                    "process not found");
            }

            var actualProcessId =
                processReader.GetGuid(0);

            var flowVersionId =
                processReader.GetGuid(1);

            var processState =
                processReader.GetString(2);

            await processReader.DisposeAsync();

            var payloadHash =
                Database.Sha256Hex(
                    payload!.RootElement.GetRawText());

            await using var existingSignalCommand =
                new NpgsqlCommand(
                    """
                    SELECT
                        process_id,
                        signal_type,
                        body_hash
                    FROM workflow.signals
                    WHERE message_id = @messageId
                    FOR UPDATE
                    """,
                    conn,
                    tx);

            existingSignalCommand.Parameters.AddWithValue(
                "messageId",
                messageId!);

            await using var existingSignalReader =
                await existingSignalCommand.ExecuteReaderAsync();

            if (await existingSignalReader.ReadAsync())
            {
                var existingProcessId =
                    existingSignalReader.GetGuid(0);

                var existingSignalType =
                    existingSignalReader.GetString(1);

                var existingBodyHash =
                    existingSignalReader.GetString(2);

                await existingSignalReader.DisposeAsync();

                if (existingProcessId != actualProcessId ||
                    existingSignalType != signalType ||
                    existingBodyHash != payloadHash)
                {
                    await tx.RollbackAsync();

                    return Fail(
                        "flow.conflict",
                        "message-id belongs to a different signal");
                }

                await tx.RollbackAsync();

                Console.WriteLine(
                    Envelope.Ok(
                        new
                        {
                            resource = "signal",
                            processId =
                                actualProcessId.ToString(),
                            messageId,
                            signalType,
                            status = "duplicate"
                        }));

                return 0;
            }

            await using var signalDefinitionCommand =
                new NpgsqlCommand(
                    """
                    SELECT count(*)
                    FROM workflow.step_definitions
                    WHERE flow_version_id = @flowVersionId
                      AND step_type = 'wait_signal'
                      AND step_config ->> 'signal_type' = @signalType
                    """,
                    conn,
                    tx);

            signalDefinitionCommand.Parameters.AddWithValue(
                "flowVersionId",
                flowVersionId);

            signalDefinitionCommand.Parameters.AddWithValue(
                "signalType",
                signalType!);

            var declared =
                (long)(await signalDefinitionCommand.ExecuteScalarAsync())!;

            if (declared == 0)
            {
                await tx.RollbackAsync();

                return Fail(
                    "flow.signal_not_declared",
                    "signal type is not declared by the pinned workflow");
            }

            await using var insertSignalCommand =
                new NpgsqlCommand(
                    """
                    INSERT INTO workflow.signals(
                        message_id,
                        process_id,
                        signal_type,
                        body,
                        body_hash,
                        status
                    )
                    VALUES (
                        @messageId,
                        @processId,
                        @signalType,
                        @body::jsonb,
                        @bodyHash,
                        'ACCEPTED'
                    )
                    """,
                    conn,
                    tx);

            insertSignalCommand.Parameters.AddWithValue(
                "messageId",
                messageId!);

            insertSignalCommand.Parameters.AddWithValue(
                "processId",
                actualProcessId);

            insertSignalCommand.Parameters.AddWithValue(
                "signalType",
                signalType!);

            insertSignalCommand.Parameters.AddWithValue(
                "body",
                payload.RootElement.GetRawText());

            insertSignalCommand.Parameters.AddWithValue(
                "bodyHash",
                payloadHash);

            await insertSignalCommand.ExecuteNonQueryAsync();

            if (processState == "WAITING_SIGNAL")
            {
                await ApplyWaitingSignalAsync(
                    conn,
                    tx,
                    actualProcessId,
                    flowVersionId,
                    signalType!,
                    messageId!);
            }

            await tx.CommitAsync();

            Console.WriteLine(
                Envelope.Ok(
                    new
                    {
                        resource = "signal",
                        processId =
                            actualProcessId.ToString(),
                        messageId,
                        signalType,
                        status = "accepted"
                    }));

            return 0;
        }
        catch (PostgresException ex)
        {
            await tx.RollbackAsync();

            return Fail(
                "flow.signal_failed",
                ex.SqlState == "23505"
                    ? "signal already exists"
                    : "failed to accept signal");
        }
        catch
        {
            await tx.RollbackAsync();

            return Fail(
                "flow.signal_failed",
                "failed to accept signal");
        }
        finally
        {
            payload.Dispose();
        }
    }

    private static async Task ApplyWaitingSignalAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid processId,
        Guid flowVersionId,
        string signalType,
        string messageId)
    {
        await using var currentStepCommand =
            new NpgsqlCommand(
                """
                SELECT
                    s.step_instance_id,
                    s.step_key,
                    sd.step_config
                FROM workflow.step_instances s
                JOIN workflow.step_definitions sd
                  ON sd.flow_version_id = @flowVersionId
                 AND sd.step_key = s.step_key
                WHERE s.process_id = @processId
                  AND s.state = 'WAITING'
                  AND s.step_type = 'WAIT_SIGNAL'
                  AND sd.step_config ->> 'signal_type' = @signalType
                ORDER BY s.entered_at DESC
                LIMIT 1
                FOR UPDATE
                """,
                conn,
                tx);

        currentStepCommand.Parameters.AddWithValue(
            "flowVersionId",
            flowVersionId);

        currentStepCommand.Parameters.AddWithValue(
            "processId",
            processId);

        currentStepCommand.Parameters.AddWithValue(
            "signalType",
            signalType);

        await using var stepReader =
            await currentStepCommand.ExecuteReaderAsync();

        if (!await stepReader.ReadAsync())
        {
            await stepReader.DisposeAsync();
            return;
        }

        var stepInstanceId =
            stepReader.GetGuid(0);

        var stepKey =
            stepReader.GetString(1);

        var config =
            stepReader.GetFieldValue<JsonDocument>(2);

        await stepReader.DisposeAsync();

        var outcome =
            GetRequiredString(
                config.RootElement,
                "outcome");

        await using var transitionCommand =
            new NpgsqlCommand(
                """
                SELECT to_step_key
                FROM workflow.transition_definitions
                WHERE flow_version_id = @flowVersionId
                  AND from_step_key = @fromStep
                  AND outcome = @outcome
                """,
                conn,
                tx);

        transitionCommand.Parameters.AddWithValue(
            "flowVersionId",
            flowVersionId);

        transitionCommand.Parameters.AddWithValue(
            "fromStep",
            stepKey);

        transitionCommand.Parameters.AddWithValue(
            "outcome",
            outcome);

        var nextStepKey =
            (string?)await transitionCommand.ExecuteScalarAsync();

        if (nextStepKey is null)
        {
            throw new InvalidOperationException(
                "workflow transition not found for signal outcome");
        }

        await using var completeStepCommand =
            new NpgsqlCommand(
                """
                UPDATE workflow.step_instances
                SET state = 'COMPLETED',
                    outcome = @outcome,
                    completed_at = clock_timestamp()
                WHERE step_instance_id = @stepInstanceId
                """,
                conn,
                tx);

        completeStepCommand.Parameters.AddWithValue(
            "outcome",
            outcome);

        completeStepCommand.Parameters.AddWithValue(
            "stepInstanceId",
            stepInstanceId);

        await completeStepCommand.ExecuteNonQueryAsync();

        await using var markSignalCommand =
            new NpgsqlCommand(
                """
                UPDATE workflow.signals
                SET status = 'APPLIED',
                    applied_at = clock_timestamp()
                WHERE message_id = @messageId
                """,
                conn,
                tx);

        markSignalCommand.Parameters.AddWithValue(
            "messageId",
            messageId);

        await markSignalCommand.ExecuteNonQueryAsync();

        await CreateNextStepAsync(
            conn,
            tx,
            processId,
            flowVersionId,
            nextStepKey);
    }

    private static async Task CreateNextStepAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid processId,
        Guid flowVersionId,
        string nextStepKey)
    {
        await using var stepCommand =
            new NpgsqlCommand(
                """
                SELECT
                    sd.step_definition_id,
                    sd.step_key,
                    sd.step_type,
                    sd.step_config
                FROM workflow.step_definitions sd
                WHERE sd.flow_version_id = @flowVersionId
                  AND sd.step_key = @stepKey
                """,
                conn,
                tx);

        stepCommand.Parameters.AddWithValue(
            "flowVersionId",
            flowVersionId);

        stepCommand.Parameters.AddWithValue(
            "stepKey",
            nextStepKey);

        await using var reader =
            await stepCommand.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            await reader.DisposeAsync();

            throw new InvalidOperationException(
                "workflow next step does not exist");
        }

        var stepDefinitionId =
            reader.GetGuid(0);

        var stepType =
            reader.GetString(2);

        var stepConfig =
            reader.GetFieldValue<JsonDocument>(3);

        await reader.DisposeAsync();

        var stepInstanceId =
            Guid.NewGuid();

        var state =
            stepType switch
            {
                "automatic" => "READY",
                "wait_signal" => "WAITING",
                "manual" => "WAITING",
                "end" => "COMPLETED",
                _ => throw new InvalidOperationException(
                    $"unsupported step type: {stepType}")
            };

        await using var insertStepCommand =
            new NpgsqlCommand(
                """
                INSERT INTO workflow.step_instances(
                    step_instance_id,
                    process_id,
                    step_key,
                    step_type,
                    state
                )
                VALUES (
                    @stepInstanceId,
                    @processId,
                    @stepKey,
                    @stepType,
                    @state
                )
                """,
                conn,
                tx);

        insertStepCommand.Parameters.AddWithValue(
            "stepInstanceId",
            stepInstanceId);

        insertStepCommand.Parameters.AddWithValue(
            "processId",
            processId);

        insertStepCommand.Parameters.AddWithValue(
            "stepKey",
            nextStepKey);

        insertStepCommand.Parameters.AddWithValue(
            "stepType",
            stepType.ToUpperInvariant());

        insertStepCommand.Parameters.AddWithValue(
            "state",
            state);

        await insertStepCommand.ExecuteNonQueryAsync();

        if (stepType == "automatic")
        {
            await using var taskCommand =
                new NpgsqlCommand(
                    """
                    SELECT task_definition_id
                    FROM workflow.task_definitions
                    WHERE step_definition_id = @stepDefinitionId
                    """,
                    conn,
                    tx);

            taskCommand.Parameters.AddWithValue(
                "stepDefinitionId",
                stepDefinitionId);

            var taskExists =
                await taskCommand.ExecuteScalarAsync();

            if (taskExists is null)
            {
                throw new InvalidOperationException(
                    "automatic step has no task definition");
            }

            await using var jobCommand =
                new NpgsqlCommand(
                    """
                    INSERT INTO workflow.jobs(
                        job_id,
                        process_id,
                        step_instance_id,
                        execution_id,
                        state,
                        lease_version,
                        attempt_count,
                        failure_count,
                        next_attempt_at
                    )
                    VALUES (
                        gen_random_uuid(),
                        @processId,
                        @stepInstanceId,
                        gen_random_uuid(),
                        'READY',
                        0,
                        0,
                        0,
                        clock_timestamp()
                    )
                    """,
                    conn,
                    tx);

            jobCommand.Parameters.AddWithValue(
                "processId",
                processId);

            jobCommand.Parameters.AddWithValue(
                "stepInstanceId",
                stepInstanceId);

            await jobCommand.ExecuteNonQueryAsync();

            await using var processCommand =
                new NpgsqlCommand(
                    """
                    UPDATE workflow.process_instances
                    SET state = 'RUNNING',
                        current_step_key = @stepKey,
                        updated_at = clock_timestamp()
                    WHERE process_id = @processId
                    """,
                    conn,
                    tx);

            processCommand.Parameters.AddWithValue(
                "stepKey",
                nextStepKey);

            processCommand.Parameters.AddWithValue(
                "processId",
                processId);

            await processCommand.ExecuteNonQueryAsync();

            return;
        }

        if (stepType == "wait_signal")
        {
            await using var processCommand =
                new NpgsqlCommand(
                    """
                    UPDATE workflow.process_instances
                    SET state = 'WAITING_SIGNAL',
                        current_step_key = @stepKey,
                        updated_at = clock_timestamp()
                    WHERE process_id = @processId
                    """,
                    conn,
                    tx);

            processCommand.Parameters.AddWithValue(
                "stepKey",
                nextStepKey);

            processCommand.Parameters.AddWithValue(
                "processId",
                processId);

            await processCommand.ExecuteNonQueryAsync();

            return;
        }

        if (stepType == "manual")
        {
            await using var processCommand =
                new NpgsqlCommand(
                    """
                    UPDATE workflow.process_instances
                    SET state = 'WAITING_MANUAL',
                        current_step_key = @stepKey,
                        updated_at = clock_timestamp()
                    WHERE process_id = @processId
                    """,
                    conn,
                    tx);

            processCommand.Parameters.AddWithValue(
                "stepKey",
                nextStepKey);

            processCommand.Parameters.AddWithValue(
                "processId",
                processId);

            await processCommand.ExecuteNonQueryAsync();

            return;
        }

        if (stepType == "end")
        {
            var outcome =
                GetOptionalString(
                    stepConfig.RootElement,
                    "outcome");

            await using var completeCommand =
                new NpgsqlCommand(
                    """
                    UPDATE workflow.step_instances
                    SET outcome = @outcome,
                        completed_at = clock_timestamp()
                    WHERE step_instance_id = @stepInstanceId
                    """,
                    conn,
                    tx);

            completeCommand.Parameters.AddWithValue(
                "outcome",
                (object?)outcome ??
                DBNull.Value);

            completeCommand.Parameters.AddWithValue(
                "stepInstanceId",
                stepInstanceId);

            await completeCommand.ExecuteNonQueryAsync();

            await using var processCommand =
                new NpgsqlCommand(
                    """
                    UPDATE workflow.process_instances
                    SET state = 'COMPLETED',
                        current_step_key = @stepKey,
                        updated_at = clock_timestamp()
                    WHERE process_id = @processId
                    """,
                    conn,
                    tx);

            processCommand.Parameters.AddWithValue(
                "stepKey",
                nextStepKey);

            processCommand.Parameters.AddWithValue(
                "processId",
                processId);

            await processCommand.ExecuteNonQueryAsync();
        }
    }

    private static bool TryParseStartArguments(
        string[] args,
        out string? flowName,
        out string? businessKey,
        out string? dataPath,
        out string? error)
    {
        flowName = null;
        businessKey = null;
        dataPath = null;
        error = null;

        if (args.Length < 3)
        {
            error =
                "usage: flow start <flow> --business-key <key> [--data <file>]";

            return false;
        }

        flowName = args[0];

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--business-key":
                    if (i + 1 >= args.Length)
                    {
                        error =
                            "--business-key requires a value";

                        return false;
                    }

                    businessKey = args[++i];
                    break;

                case "--data":
                    if (i + 1 >= args.Length)
                    {
                        error =
                            "--data requires a value";

                        return false;
                    }

                    dataPath = args[++i];
                    break;

                default:
                    error =
                        $"unexpected argument: {args[i]}";

                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(flowName))
        {
            error = "flow name is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(businessKey))
        {
            error = "--business-key is required";
            return false;
        }

        dataPath ??= "/dev/stdin";

        return true;
    }

    private static bool TryParseSignalArguments(
        string[] args,
        out Guid processId,
        out string? signalType,
        out string? messageId,
        out string? payloadPath,
        out string? error)
    {
        processId = Guid.Empty;
        signalType = null;
        messageId = null;
        payloadPath = null;
        error = null;

        if (args.Length < 7 ||
            !Guid.TryParse(args[0], out processId))
        {
            error =
                "usage: flow signal <process-id> --type <type> --message-id <id> --payload <file>";

            return false;
        }

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--type":
                    if (i + 1 >= args.Length)
                    {
                        error = "--type requires a value";
                        return false;
                    }

                    signalType = args[++i];
                    break;

                case "--message-id":
                    if (i + 1 >= args.Length)
                    {
                        error =
                            "--message-id requires a value";

                        return false;
                    }

                    messageId = args[++i];
                    break;

                case "--payload":
                    if (i + 1 >= args.Length)
                    {
                        error =
                            "--payload requires a value";

                        return false;
                    }

                    payloadPath = args[++i];
                    break;

                default:
                    error =
                        $"unexpected argument: {args[i]}";

                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(signalType))
        {
            error = "--type is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(messageId))
        {
            error = "--message-id is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            error = "--payload is required";
            return false;
        }

        return true;
    }

    private static async Task<(JsonDocument? Document, string? Error)>
        TryReadJsonObjectAsync(
            string path)
    {
        try
        {
            string text;

            if (path == "/dev/stdin")
            {
                text =
                    await Console.In.ReadToEndAsync();
            }
            else
            {
                if (!File.Exists(path))
                {
                    return (
                        null,
                        $"file not found: {path}");
                }

                text =
                    await File.ReadAllTextAsync(path);
            }

            var document =
                JsonDocument.Parse(text);

            if (document.RootElement.ValueKind !=
                JsonValueKind.Object)
            {
                document.Dispose();

                return (
                    null,
                    "data must be a JSON object");
            }

            return (
                document,
                null);
        }
        catch (JsonException ex)
        {
            return (
                null,
                $"invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (
                null,
                $"failed to read JSON: {ex.Message}");
        }
    }

    private static bool JsonDocumentsEqual(
        JsonDocument left,
        JsonDocument right)
    {
        return string.Equals(
            left.RootElement.GetRawText(),
            right.RootElement.GetRawText(),
            StringComparison.Ordinal);
    }

    private static string GetRequiredString(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"required string property '{property}' is missing");
        }

        return value.GetString()!;
    }

    private static string? GetOptionalString(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int Fail(
        string code,
        string message)
    {
        Console.WriteLine(
            Envelope.Error(
                code,
                message));

        return 1;
    }
}