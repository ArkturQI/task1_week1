using Cli.Commands;
using Cli.Services;

var cmd = args.Length > 0
    ? args[0]
    : null;

var sub = args.Length > 1
    ? args[1]
    : null;

if (cmd is null)
{
    Console.WriteLine(
        "{\"status\":\"ok\",\"message\":\"cli ready — no command provided, entering keep-alive mode\"}");

    while (true)
    {
        await Task.Delay(Timeout.Infinite);
    }
}

try
{
    switch (cmd)
    {
        case "ping":
            {
                Console.WriteLine(
                    Envelope.Ok(
                        new
                        {
                            pong = true
                        }));

                return 0;
            }

        case "migration":
            {
                if (sub == "apply")
                {
                    return await MigrationCommands.ApplyAsync(
                        args.Length > 2
                            ? args[2]
                            : null);
                }

                return Fail(
                    "cli.unknown_subcommand",
                    "unknown migration subcommand: " + sub);
            }

        case "action":
            {
                switch (sub)
                {
                    case "validate":
                        return ActionCommands.Validate(
                            args.Length > 2
                                ? args[2]
                                : null);

                    case "publish":
                        return await ActionCommands.PublishAsync(
                            args.Length > 2
                                ? args[2]
                                : null);

                    case "list":
                        return await ActionCommands.ListAsync();

                    case "activate":
                    case "disable":
                        return await ActionCommands.LifecycleAsync(
                            sub,
                            args.Skip(2).ToArray());

                    default:
                        return Fail(
                            "cli.unknown_subcommand",
                            "unknown action subcommand: " + sub);
                }
            }

        case "flow":
            {
                switch (sub)
                {
                    case "validate":
                        return await FlowCommands.ValidateAsync(
                            args.Length > 2
                                ? args[2]
                                : null);

                    case "publish":
                        return await FlowCommands.PublishAsync(
                            args.Length > 2
                                ? args[2]
                                : null);

                    case "list":
                        return await FlowCommands.ListAsync();

                    case "activate":
                        return await FlowCommands.ActivateAsync(
                            args.Skip(2).ToArray());

                    default:
                        return Fail(
                            "cli.unknown_subcommand",
                            "unknown flow subcommand: " + sub);
                }
            }

        default:
            return Fail(
                "cli.unknown_command",
                "unknown command: " + cmd);
    }
}
catch (Exception ex)
{
    return Fail(
        "cli.internal",
        ex.Message);
}

static int Fail(
    string code,
    string message)
{
    Console.WriteLine(
        Envelope.Error(
            code,
            message));

    return 1;
}