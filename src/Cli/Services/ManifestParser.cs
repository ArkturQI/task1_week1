using System.Text.Json;
using System.Text.RegularExpressions;
using Cli.Models;

namespace Cli.Services;

internal static class ManifestParser
{
    private static readonly Regex SqlIdentRegex = new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);
    private static readonly Regex OutcomeRegex = new("^[A-Z][A-Z0-9_]{0,62}$", RegexOptions.Compiled);
    private static readonly Regex PolicyRegex = new("^[a-z][a-z0-9_-]*:[a-z][a-z0-9_-]*$", RegexOptions.Compiled);

    public static bool TryParse(string? path, out ManifestInfo? info, out string? error)
    {
        info = null;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            error = "file not found: " + path;
            return false;
        }

        var content = File.ReadAllText(path);
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(content).RootElement;
        }
        catch (Exception e)
        {
            error = "invalid JSON: " + e.Message;
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "manifest must be a JSON object";
            return false;
        }

        if (!root.TryGetProperty("contract_version", out var cv) || cv.GetString() != "course-1")
        {
            error = "contract_version must be 'course-1'";
            return false;
        }

        string? module = root.TryGetProperty("module", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        string? action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        int version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vi) ? vi : -1;

        if (string.IsNullOrEmpty(module) || !SqlIdentRegex.IsMatch(module)) { error = "missing or invalid module identifier"; return false; }
        if (string.IsNullOrEmpty(action) || !SqlIdentRegex.IsMatch(action)) { error = "missing or invalid action identifier"; return false; }
        if (version < 1) { error = "version must be an integer >= 1"; return false; }

        string? targetSchema = root.TryGetProperty("target_schema", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() : null;
        string? targetFunction = root.TryGetProperty("target_function", out var tf) && tf.ValueKind == JsonValueKind.String ? tf.GetString() : null;
        if (string.IsNullOrEmpty(targetSchema) || !SqlIdentRegex.IsMatch(targetSchema)) { error = "missing or invalid target_schema identifier"; return false; }
        if (string.IsNullOrEmpty(targetFunction) || !SqlIdentRegex.IsMatch(targetFunction)) { error = "missing or invalid target_function identifier"; return false; }

        if (!root.TryGetProperty("request_schema", out var rs) || rs.ValueKind != JsonValueKind.Object)
        { error = "missing or invalid request_schema"; return false; }
        if (!root.TryGetProperty("response_schema", out var ss) || ss.ValueKind != JsonValueKind.Object)
        { error = "missing or invalid response_schema"; return false; }
        if (!root.TryGetProperty("outcomes", out var oc) || oc.ValueKind != JsonValueKind.Array || oc.GetArrayLength() == 0)
        { error = "outcomes must be a non-empty array"; return false; }

        foreach (var el in oc.EnumerateArray())
        {
            var str = el.GetString();
            if (string.IsNullOrEmpty(str) || !OutcomeRegex.IsMatch(str))
            { error = $"invalid outcome format: {str}"; return false; }
        }

        var enabled = !root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
        var isDefault = root.TryGetProperty("is_default", out var id) && id.ValueKind == JsonValueKind.True;
        if (isDefault && !enabled)
        {
            error = "is_default=true requires enabled=true";
            return false;
        }

        var httpMethod = root.TryGetProperty("http_method", out var hm) && hm.ValueKind == JsonValueKind.String ? hm.GetString() : "POST";
        var outcomes = oc.GetRawText();

        info = new ManifestInfo
        {
            Module = module!,
            Action = action!,
            Version = version,
            Hash = Database.Sha256Hex(content),
            Content = content,
            Enabled = enabled,
            IsDefault = isDefault,
            ManifestSize = content.Length,
            HttpMethod = httpMethod ?? "POST",
            TargetSchema = targetSchema!,
            TargetFunction = targetFunction!,
            Outcomes = outcomes
        };

        error = null;
        return true;
    }
}