using System.Text.Json;
using Cli.Models;

namespace Cli.Services;

internal static class ManifestParser
{
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

        string? module = root.TryGetProperty("module", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        string? action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        int version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vi) ? vi : -1;

        if (string.IsNullOrEmpty(module)) { error = "missing or invalid module"; return false; }
        if (string.IsNullOrEmpty(action)) { error = "missing or invalid action"; return false; }
        if (version < 1) { error = "missing or invalid version"; return false; }
        if (!root.TryGetProperty("request_schema", out var rs) || rs.ValueKind != JsonValueKind.Object)
        { error = "missing or invalid request_schema"; return false; }
        if (!root.TryGetProperty("response_schema", out var ss) || ss.ValueKind != JsonValueKind.Object)
        { error = "missing or invalid response_schema"; return false; }

        var enabled = !root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
        var isDefault = root.TryGetProperty("is_default", out var id) && id.ValueKind == JsonValueKind.True;

        info = new ManifestInfo
        {
            Module = module!,
            Action = action!,
            Version = version,
            Hash = Database.Sha256Hex(content),
            Content = content,
            Enabled = enabled,
            IsDefault = isDefault,
            ManifestSize = content.Length
        };
        error = null;
        return true;
    }
}