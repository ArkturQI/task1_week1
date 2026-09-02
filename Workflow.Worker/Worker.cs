using Microsoft.Extensions.Hosting;
using Workflow.Worker.Configuration;
using Workflow.Worker.Execution;
using Workflow.Worker.Infrastructure;

namespace Workflow.Worker;

public sealed class Worker : BackgroundService
{
    private readonly WorkerOptions _options;
    private readonly WorkflowRepository _repository;
    private readonly WorkflowExecutor _executor;
    private readonly ILogger<Worker> _logger;

    public Worker(
        WorkerOptions options,
        WorkflowRepository repository,
        WorkflowExecutor executor,
        ILogger<Worker> logger)
    {
        _options = options;
        _repository = repository;
        _executor = executor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Workflow worker {Owner} started",
            _options.Owner);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobs =
                    await _repository.ClaimJobsAsync(
                        _options.Owner,
                        _options.ClaimLimit,
                        _options.LeaseSeconds,
                        stoppingToken);

                if (jobs.Count == 0)
                {
                    await Task.Delay(
                        _options.PollIntervalMs,
                        stoppingToken);

                    continue;
                }

                foreach (var originalJob in jobs)
                {
                    var job =
                        originalJob with
                        {
                            Owner = _options.Owner
                        };

                    if (ShouldPauseAtFailpoint(
                            "after_job_claim"))
                    {
                        _logger.LogWarning(
                            "Failpoint after_job_claim reached by {Owner} for job {JobId}",
                            _options.Owner,
                            job.JobId);

                        await Task.Delay(
                            Timeout.Infinite,
                            stoppingToken);
                    }

                    await ExecuteJobAsync(
                        job,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Workflow worker {Owner} tick failed",
                    _options.Owner);

                await Task.Delay(
                    _options.PollIntervalMs,
                    stoppingToken);
            }
        }

        _logger.LogInformation(
            "Workflow worker {Owner} stopped",
            _options.Owner);
    }

    private async Task ExecuteJobAsync(
        ClaimedJob job,
        CancellationToken cancellationToken)
    {
        try
        {
            await _executor.ExecuteAsync(
                job,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ActionInvocationException ex)
        {
            _logger.LogWarning(
                ex,
                "Action failed for job {JobId}: {Code}",
                job.JobId,
                ex.Code);

            var retryable =
                ex.Retryable ??
                IsRetryable(
                    ex.Code);

            await TryFailJobAsync(
                job,
                ex.Code,
                retryable,
                cancellationToken);
        }
        catch (WorkflowExecutionException ex)
        {
            _logger.LogError(
                ex,
                "Workflow execution failed for job {JobId}",
                job.JobId);

            await TryFailJobAsync(
                job,
                "worker.execution_failed",
                true,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected worker error for job {JobId}",
                job.JobId);

            await TryFailJobAsync(
                job,
                "worker.exception",
                true,
                cancellationToken);
        }
    }

    private async Task TryFailJobAsync(
        ClaimedJob job,
        string errorCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        try
        {
            var failure =
                await _repository.FailJobAsync(
                    job,
                    errorCode,
                    retryable,
                    cancellationToken);

            _logger.LogWarning(
                "Job {JobId} failed. operation={Operation}, retryable={Retryable}, attempt={Attempt}",
                job.JobId,
                failure.Operation,
                retryable,
                failure.AttemptNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not record failure for job {JobId}",
                job.JobId);
        }
    }

    private static bool IsRetryable(
        string code)
    {
        return code switch
        {
            "dependency.unavailable" => true,
            "action.timeout" => true,
            "internal.error" => true,
            "worker.execution_failed" => true,
            "worker.exception" => true,
            _ => false
        };
    }

    private bool ShouldPauseAtFailpoint(
        string name)
    {
        if (!_options.TestProfile)
            return false;

        return string.Equals(
            _options.Failpoint,
            name,
            StringComparison.Ordinal);
    }
}
