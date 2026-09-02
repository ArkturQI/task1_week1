using System.Text.Json;
using Npgsql;
using Workflow.Worker.Infrastructure;

namespace Workflow.Worker.Execution;

public sealed class WorkflowExecutor
{
    private readonly string _connectionString;
    private readonly PayloadMapper _payloadMapper;
    private readonly ILogger<WorkflowExecutor> _logger;

    public WorkflowExecutor(
        string connectionString,
        PayloadMapper payloadMapper,
        ILogger<WorkflowExecutor> logger)
    {
        _connectionString = connectionString;
        _payloadMapper = payloadMapper;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        ClaimedJob job,
        CancellationToken cancellationToken)
    {
        using var payload =
            _payloadMapper.BuildPayload(
                job.ProcessData,
                job.InputMapping,
                job.InputConstants);

        var correlationId =
            Guid.NewGuid();

        var requestId =
            job.ExecutionId.ToString();

        var idempotencyKey =
            job.ExecutionId.ToString();

        /*
         * ВАЖНО:
         * executionId / correlationId / requestId / idempotencyKey
         * должны находиться в корне context.
         *
         * Fixture target function читает:
         *
         *   p_context ->> 'executionId'
         *
         * а не workflow.executionId.
         */
        using var context =
            JsonSerializer.SerializeToDocument(
                new
                {
                    executionId =
                        job.ExecutionId.ToString(),

                    correlationId =
                        correlationId.ToString(),

                    requestId,

                    idempotencyKey,

                    principal =
                        "workflow-worker",

                    consumer =
                        "workflow-worker",

                    scopes = new[]
                    {
                        "workflow:execute",
                        "workflow:read"
                    },

                    workflow = new
                    {
                        processId =
                            job.ProcessId.ToString(),

                        flowVersionId =
                            job.FlowVersionId.ToString(),

                        step =
                            job.StepKey,

                        executionId =
                            job.ExecutionId.ToString(),

                        attemptId =
                            job.AttemptId.ToString(),

                        leaseVersion =
                            job.LeaseVersion
                    }
                });

        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            new NpgsqlCommand(
                """
                SELECT api.invoke(
                    @module,
                    @action,
                    @version,
                    @context::jsonb,
                    @payload::jsonb
                )::text
                """,
                connection);

        command.CommandTimeout =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    job.TimeoutMs / 1000.0));

        command.Parameters.AddWithValue(
            "module",
            job.Module);

        command.Parameters.AddWithValue(
            "action",
            job.Action);

        command.Parameters.AddWithValue(
            "version",
            job.ActionVersion);

        command.Parameters.AddWithValue(
            "context",
            context.RootElement.GetRawText());

        command.Parameters.AddWithValue(
            "payload",
            payload.RootElement.GetRawText());

        _logger.LogInformation(
            "Executing job {JobId}, execution {ExecutionId}, attempt {AttemptId}, leaseVersion {LeaseVersion}, action {Module}.{Action} v{Version}",
            job.JobId,
            job.ExecutionId,
            job.AttemptId,
            job.LeaseVersion,
            job.Module,
            job.Action,
            job.ActionVersion);

        string raw;

        try
        {
            raw =
                await command.ExecuteScalarAsync(
                    cancellationToken) as string
                ?? "{}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkflowExecutionException(
                "action invocation failed",
                ex);
        }

        using var response =
            JsonDocument.Parse(raw);

        var root =
            response.RootElement;

        if (root.TryGetProperty(
                "status",
                out var statusElement) &&
            string.Equals(
                statusElement.GetString(),
                "error",
                StringComparison.OrdinalIgnoreCase))
        {
            var code =
                root.TryGetProperty(
                    "code",
                    out var codeElement)
                    ? codeElement.GetString()
                    : null;

            var message =
                root.TryGetProperty(
                    "message",
                    out var messageElement)
                    ? messageElement.GetString()
                    : null;

            /*
             * Action сам знает, повторяема ли его ошибка (например,
             * fixture.retry_a7dfc8eb -> retryable: true,
             * fixture.error_09b64e2f -> retryable: false).
             * Это явное решение из тела ответа имеет приоритет над
             * статической классификацией по коду в Worker.IsRetryable,
             * которая касается только внутренних инфраструктурных ошибок
             * воркера, а не бизнес-ошибок конкретного action.
             */
            bool? retryable =
                root.TryGetProperty(
                    "retryable",
                    out var retryableElement) &&
                (retryableElement.ValueKind == JsonValueKind.True ||
                 retryableElement.ValueKind == JsonValueKind.False)
                    ? retryableElement.GetBoolean()
                    : null;

            throw new ActionInvocationException(
                code ?? "action.error",
                message ?? "action invocation returned an error",
                retryable);
        }

        if (!root.TryGetProperty(
                "outcome",
                out var outcomeElement) ||
            outcomeElement.ValueKind !=
                JsonValueKind.String)
        {
            throw new WorkflowExecutionException(
                "api.invoke response does not contain outcome");
        }

        var outcome =
            outcomeElement.GetString();

        if (string.IsNullOrWhiteSpace(outcome))
        {
            throw new WorkflowExecutionException(
                "api.invoke returned an empty outcome");
        }

        JsonElement result;

        if (root.TryGetProperty(
                "result",
                out var resultElement))
        {
            result =
                resultElement.Clone();
        }
        else
        {
            using var empty =
                JsonDocument.Parse("{}");

            result =
                empty.RootElement.Clone();
        }

        await using var finishConnection =
            new NpgsqlConnection(
                _connectionString);

        await finishConnection.OpenAsync(
            cancellationToken);

        await using var finishCommand =
            new NpgsqlCommand(
                """
                SELECT workflow.finish_job(
                    @jobId,
                    @owner,
                    @leaseVersion,
                    @outcome,
                    @result::jsonb
                )::text
                """,
                finishConnection);

        finishCommand.Parameters.AddWithValue(
            "jobId",
            job.JobId);

        finishCommand.Parameters.AddWithValue(
            "owner",
            job.Owner);

        finishCommand.Parameters.AddWithValue(
            "leaseVersion",
            job.LeaseVersion);

        finishCommand.Parameters.AddWithValue(
            "outcome",
            outcome);

        finishCommand.Parameters.AddWithValue(
            "result",
            result.GetRawText());

        var finishRaw =
            await finishCommand.ExecuteScalarAsync(
                cancellationToken) as string
            ?? "{}";

        _logger.LogInformation(
            "Job {JobId} finished with outcome {Outcome}. Result: {FinishResult}",
            job.JobId,
            outcome,
            finishRaw);
    }
}

public sealed class WorkflowExecutionException :
    Exception
{
    public WorkflowExecutionException(
        string message)
        : base(message)
    {
    }

    public WorkflowExecutionException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ActionInvocationException :
    Exception
{
    public ActionInvocationException(
        string code,
        string message,
        bool? retryable = null)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    /// <summary>
    /// Explicit retryable decision from the action's own error envelope,
    /// when it provided one. Null means the action didn't say - fall back
    /// to Worker.IsRetryable's static classification by code.
    /// </summary>
    public bool? Retryable { get; }
}