using System.Data;
using System.Text.Json;
using Npgsql;

namespace Workflow.Worker.Infrastructure;

public sealed class WorkflowRepository
{
    private readonly string _connectionString;

    public WorkflowRepository(
        string connectionString)
    {
        _connectionString =
            connectionString
            ?? throw new ArgumentNullException(
                nameof(connectionString));
    }

    public async Task<IReadOnlyList<ClaimedJob>> ClaimJobsAsync(
        string owner,
        int limit,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            new NpgsqlCommand(
                """
                SELECT
                    job_id,
                    process_id,
                    step_instance_id,
                    execution_id,
                    lease_version,
                    attempt_id,
                    attempt_number,
                    lease_until,
                    module,
                    action,
                    action_version,
                    required_policy,
                    timeout_ms,
                    retry_max_attempts,
                    retry_delays_ms,
                    input_mapping,
                    input_constants,
                    process_data,
                    flow_version_id,
                    step_key,
                    step_type,
                    step_config
                FROM workflow.claim_jobs(
                    @owner,
                    @limit,
                    @leaseSeconds
                )
                """,
                connection);

        command.Parameters.AddWithValue(
            "owner",
            owner);

        command.Parameters.AddWithValue(
            "limit",
            limit);

        command.Parameters.AddWithValue(
            "leaseSeconds",
            leaseSeconds);

        var jobs =
            new List<ClaimedJob>();

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            jobs.Add(
                new ClaimedJob
                {
                    JobId = reader.GetGuid(0),
                    ProcessId = reader.GetGuid(1),
                    StepInstanceId = reader.GetGuid(2),
                    ExecutionId = reader.GetGuid(3),
                    LeaseVersion = reader.GetInt64(4),
                    AttemptId = reader.GetGuid(5),
                    AttemptNumber = reader.GetInt32(6),
                    LeaseUntil = reader.GetFieldValue<DateTime>(7),
                    Module = reader.GetString(8),
                    Action = reader.GetString(9),
                    ActionVersion = reader.GetInt32(10),
                    RequiredPolicy =
                        reader.GetFieldValue<JsonDocument>(11),
                    TimeoutMs = reader.GetInt32(12),
                    RetryMaxAttempts = reader.GetInt32(13),
                    RetryDelaysMs =
                        reader.GetFieldValue<JsonDocument>(14),
                    InputMapping =
                        reader.GetFieldValue<JsonDocument>(15),
                    InputConstants =
                        reader.GetFieldValue<JsonDocument>(16),
                    ProcessData =
                        reader.GetFieldValue<JsonDocument>(17),
                    FlowVersionId = reader.GetGuid(18),
                    StepKey = reader.GetString(19),
                    StepType = reader.GetString(20),
                    StepConfig =
                        reader.GetFieldValue<JsonDocument>(21)
                });
        }

        return jobs;
    }

    public async Task FinishJobAsync(
        ClaimedJob job,
        string outcome,
        JsonElement result,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            new NpgsqlCommand(
                """
                SELECT workflow.finish_job(
                    @jobId,
                    @owner,
                    @leaseVersion,
                    @outcome,
                    @result::jsonb
                )
                """,
                connection);

        command.Parameters.AddWithValue(
            "jobId",
            job.JobId);

        command.Parameters.AddWithValue(
            "owner",
            job.Owner);

        command.Parameters.AddWithValue(
            "leaseVersion",
            job.LeaseVersion);

        command.Parameters.AddWithValue(
            "outcome",
            outcome);

        command.Parameters.AddWithValue(
            "result",
            result.GetRawText());

        await command.ExecuteScalarAsync(
            cancellationToken);
    }

    public async Task<FailureResult> FailJobAsync(
        ClaimedJob job,
        string errorCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new NpgsqlConnection(_connectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            new NpgsqlCommand(
                """
                SELECT workflow.fail_job(
                    @jobId,
                    @owner,
                    @leaseVersion,
                    @errorCode,
                    @retryable
                )
                """,
                connection);

        command.Parameters.AddWithValue(
            "jobId",
            job.JobId);

        command.Parameters.AddWithValue(
            "owner",
            job.Owner);

        command.Parameters.AddWithValue(
            "leaseVersion",
            job.LeaseVersion);

        command.Parameters.AddWithValue(
            "errorCode",
            errorCode);

        command.Parameters.AddWithValue(
            "retryable",
            retryable);

        var value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        if (value is null ||
            value == DBNull.Value)
        {
            return new FailureResult
            {
                Operation = "unknown"
            };
        }

        using var document =
            value switch
            {
                JsonDocument jsonDocument =>
                    jsonDocument,

                string json =>
                    JsonDocument.Parse(json),

                _ =>
                    JsonDocument.Parse(
                        value.ToString()!)
            };

        var root =
            document.RootElement;

        return new FailureResult
        {
            Operation =
                root.TryGetProperty(
                    "operation",
                    out var operation)
                    ? operation.GetString() ?? "unknown"
                    : "unknown",

            AttemptNumber =
                root.TryGetProperty(
                    "attemptNumber",
                    out var attempt)
                    ? attempt.GetInt32()
                    : null
        };
    }
}

public sealed record ClaimedJob
{
    public Guid JobId { get; init; }

    public Guid ProcessId { get; init; }

    public Guid StepInstanceId { get; init; }

    public Guid ExecutionId { get; init; }

    public long LeaseVersion { get; init; }

    public Guid AttemptId { get; init; }

    public int AttemptNumber { get; init; }

    public DateTime LeaseUntil { get; init; }

    public string Module { get; init; } = "";

    public string Action { get; init; } = "";

    public int ActionVersion { get; init; }

    public JsonDocument RequiredPolicy { get; init; } =
        JsonDocument.Parse("[]");

    public int TimeoutMs { get; init; }

    public int RetryMaxAttempts { get; init; }

    public JsonDocument RetryDelaysMs { get; init; } =
        JsonDocument.Parse("[]");

    public JsonDocument InputMapping { get; init; } =
        JsonDocument.Parse("{}");

    public JsonDocument InputConstants { get; init; } =
        JsonDocument.Parse("{}");

    public JsonDocument ProcessData { get; init; } =
        JsonDocument.Parse("{}");

    public Guid FlowVersionId { get; init; }

    public string StepKey { get; init; } = "";

    public string StepType { get; init; } = "";

    public JsonDocument StepConfig { get; init; } =
        JsonDocument.Parse("{}");

    public string Owner { get; init; } = "";
}

public sealed class FailureResult
{
    public string Operation { get; init; } = "unknown";

    public int? AttemptNumber { get; init; }
}