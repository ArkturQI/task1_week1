using Cli.Commands;
using Cli.Services;

var cmd = args.Length > 0 ? args[0] : null;
var sub = args.Length > 1 ? args[1] : null;

try
{
    switch (cmd)
    {
        case "ping":
            Console.WriteLine(Envelope.Ok(new { pong = true }));
            return 0;

        case "migration":
            return sub switch
            {
                "apply" => await MigrationCommands.ApplyAsync(args.Length > 2 ? args[2] : null),
                _ => Fail("cli.unknown_subcommand", "unknown migration subcommand: " + sub)
            };

        case "action":
            return sub switch
            {
                "validate" => ActionCommands.Validate(args.Length > 2 ? args[2] : null),
                "publish" => await ActionCommands.PublishAsync(args.Length > 2 ? args[2] : null),
                "list" => await ActionCommands.ListAsync(),
                "activate" or "disable" => await ActionCommands.LifecycleAsync(sub, args.Skip(2).ToArray()),
                _ => Fail("cli.unknown_subcommand", "unknown action subcommand: " + sub)
            };

        default:
            return Fail("cli.unknown_command", "unknown command: " + cmd);
    }
}
catch (Exception ex)
{
    return Fail("cli.internal", ex.Message);
}

static int Fail(string code, string message)
{
    Console.WriteLine(Envelope.Error(code, message));
    return 1;
}