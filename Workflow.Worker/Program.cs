using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workflow.Worker.Configuration;
using Workflow.Worker.Execution;
using Workflow.Worker.Infrastructure;

namespace Workflow.Worker;

public static class Program
{
    public static async Task Main(
        string[] args)
    {
        var builder =
            Host.CreateApplicationBuilder(args);

        var options =
            WorkerOptions.FromEnvironment();

        builder.Services.AddSingleton(
            options);

        builder.Services.AddSingleton(
            new WorkflowRepository(
                options.ConnectionString));

        builder.Services.AddSingleton<
            PayloadMapper>();

        builder.Services.AddSingleton(
            serviceProvider =>
                new WorkflowExecutor(
                    options.ConnectionString,
                    serviceProvider.GetRequiredService<
                        PayloadMapper>(),
                    serviceProvider.GetRequiredService<
                        ILogger<WorkflowExecutor>>()));

        builder.Services.AddHostedService<Worker>();

        var host =
            builder.Build();

        await host.RunAsync();
    }
}