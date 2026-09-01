namespace Workflow.Worker.Configuration;

public sealed class WorkerOptions
{
    public string Owner { get; init; } = "worker-a";

    public string ConnectionString { get; init; } =
        "Host=postgres;Port=5432;Database=course;Username=workflow_worker_login;Password=worker_secret_change_me";

    public int PollIntervalMs { get; init; } = 500;

    public int ClaimLimit { get; init; } = 1;

    public int LeaseSeconds { get; init; } = 5;

    public bool TestProfile { get; init; }

    public string? Failpoint { get; init; }

    public static WorkerOptions FromEnvironment()
    {
        return new WorkerOptions
        {
            Owner =
                Environment.GetEnvironmentVariable(
                    "WORKER_OWNER")
                ?? "worker-a",

            ConnectionString =
                Environment.GetEnvironmentVariable(
                    "ConnectionStrings__Course")
                ?? Environment.GetEnvironmentVariable(
                    "WORKER_CONNECTION_STRING")
                ?? "Host=postgres;Port=5432;Database=course;Username=workflow_worker_login;Password=worker_secret_change_me",

            PollIntervalMs =
                ParsePositiveInt(
                    "WORKER_POLL_INTERVAL_MS",
                    500),

            ClaimLimit =
                ParsePositiveInt(
                    "WORKER_CLAIM_LIMIT",
                    1),

            LeaseSeconds =
                ParsePositiveInt(
                    "WORKER_LEASE_SECONDS",
                    5),

            TestProfile =
                ParseBool(
                    "COURSE_TEST_PROFILE"),

            Failpoint =
                Environment.GetEnvironmentVariable(
                    "COURSE_FAILPOINT")
        };
    }

    private static int ParsePositiveInt(
        string name,
        int fallback)
    {
        var value =
            Environment.GetEnvironmentVariable(name);

        return int.TryParse(value, out var parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    private static bool ParseBool(
        string name)
    {
        var value =
            Environment.GetEnvironmentVariable(name);

        return bool.TryParse(
            value,
            out var result) &&
            result;
    }
}