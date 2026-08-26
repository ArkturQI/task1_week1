using System.Text.Json;

namespace Cli.Services;

internal static class Envelope
{
    public static string Ok(object? result) =>
        Serialize("ok", result, null, null);

    public static string Error(string code, string message) =>
        Serialize("error", null, code, message);

    private static string Serialize(string status, object? result, string? code, string? message)
    {
        var env = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["meta"] = new { contractVersion = "course-1" }
        };
        if (result is not null) env["result"] = result;
        if (code is not null) env["code"] = code;
        if (message is not null) env["message"] = message;
        return JsonSerializer.Serialize(env);
    }
}