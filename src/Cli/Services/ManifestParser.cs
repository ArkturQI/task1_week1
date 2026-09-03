using System.Text.Json;
using System.Text.RegularExpressions;
using Cli.Models;

namespace Cli.Services;

internal static class ManifestParser
{
    private static readonly Regex SqlIdentRegex =
        new(
            "^[a-z][a-z0-9_]{0,62}$",
            RegexOptions.Compiled);

    private static readonly Regex OutcomeRegex =
        new(
            "^[A-Z][A-Z0-9_]{0,62}$",
            RegexOptions.Compiled);

    public static bool TryParse(
        string? path,
        out ManifestInfo? info,
        out string? error)
    {
        info = null;

        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
        {
            error = "file not found: " + path;
            return false;
        }

        string content;

        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            error = "failed to read file: " + ex.Message;
            return false;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            error = "invalid JSON: " + ex.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "manifest must be a JSON object";
                return false;
            }

            if (!root.TryGetProperty(
                    "contract_version",
                    out var contractVersion) ||
                contractVersion.ValueKind != JsonValueKind.String ||
                contractVersion.GetString() != "course-1")
            {
                error = "contract_version must be 'course-1'";
                return false;
            }

            var module =
                root.TryGetProperty(
                    "module",
                    out var moduleElement) &&
                moduleElement.ValueKind == JsonValueKind.String
                    ? moduleElement.GetString()
                    : null;

            var action =
                root.TryGetProperty(
                    "action",
                    out var actionElement) &&
                actionElement.ValueKind == JsonValueKind.String
                    ? actionElement.GetString()
                    : null;

            var version =
                root.TryGetProperty(
                        "version",
                        out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.Number &&
                versionElement.TryGetInt32(out var parsedVersion)
                    ? parsedVersion
                    : -1;

            if (string.IsNullOrWhiteSpace(module) ||
                !SqlIdentRegex.IsMatch(module))
            {
                error = "missing or invalid module identifier";
                return false;
            }

            if (string.IsNullOrWhiteSpace(action) ||
                !SqlIdentRegex.IsMatch(action))
            {
                error = "missing or invalid action identifier";
                return false;
            }

            if (version < 1)
            {
                error = "version must be an integer >= 1";
                return false;
            }

            var targetSchema =
                root.TryGetProperty(
                    "target_schema",
                    out var targetSchemaElement) &&
                targetSchemaElement.ValueKind == JsonValueKind.String
                    ? targetSchemaElement.GetString()
                    : null;

            var targetFunction =
                root.TryGetProperty(
                    "target_function",
                    out var targetFunctionElement) &&
                targetFunctionElement.ValueKind == JsonValueKind.String
                    ? targetFunctionElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(targetSchema) ||
                !SqlIdentRegex.IsMatch(targetSchema))
            {
                error =
                    "missing or invalid target_schema identifier";

                return false;
            }

            if (string.IsNullOrWhiteSpace(targetFunction) ||
                !SqlIdentRegex.IsMatch(targetFunction))
            {
                error =
                    "missing or invalid target_function identifier";

                return false;
            }

            if (!root.TryGetProperty(
                    "request_schema",
                    out var requestSchema) ||
                requestSchema.ValueKind != JsonValueKind.Object ||
                !HasDraft202012Marker(requestSchema))
            {
                error = "request_schema must be an object with $schema draft 2020-12";
                return false;
            }

            if (!root.TryGetProperty(
                    "response_schema",
                    out var responseSchema) ||
                responseSchema.ValueKind != JsonValueKind.Object ||
                !HasDraft202012Marker(responseSchema))
            {
                error = "response_schema must be an object with $schema draft 2020-12";
                return false;
            }

            if (!root.TryGetProperty(
                    "outcomes",
                    out var outcomesElement) ||
                outcomesElement.ValueKind != JsonValueKind.Array ||
                outcomesElement.GetArrayLength() == 0)
            {
                error = "outcomes must be a non-empty array";
                return false;
            }

            foreach (var outcomeElement in
                     outcomesElement.EnumerateArray())
            {
                if (outcomeElement.ValueKind != JsonValueKind.String)
                {
                    error = "outcomes must contain strings";
                    return false;
                }

                var outcome =
                    outcomeElement.GetString();

                if (string.IsNullOrWhiteSpace(outcome) ||
                    !OutcomeRegex.IsMatch(outcome))
                {
                    error =
                        $"invalid outcome format: {outcome}";

                    return false;
                }
            }

            var enabled =
                !root.TryGetProperty(
                    "enabled",
                    out var enabledElement) ||
                enabledElement.ValueKind != JsonValueKind.False;

            var isDefault =
                root.TryGetProperty(
                    "is_default",
                    out var defaultElement) &&
                defaultElement.ValueKind == JsonValueKind.True;

            if (isDefault && !enabled)
            {
                error =
                    "is_default=true requires enabled=true";

                return false;
            }

            var httpMethod =
                root.TryGetProperty(
                    "http_method",
                    out var httpMethodElement) &&
                httpMethodElement.ValueKind == JsonValueKind.String
                    ? httpMethodElement.GetString()
                    : "POST";

            info = new ManifestInfo
            {
                Module = module,
                Action = action,
                Version = version,
                Hash = Database.Sha256Hex(content),
                Content = content,
                Enabled = enabled,
                IsDefault = isDefault,
                ManifestSize = content.Length,
                HttpMethod = httpMethod ?? "POST",
                TargetSchema = targetSchema,
                TargetFunction = targetFunction,
                Outcomes = outcomesElement.GetRawText()
            };

            error = null;
            return true;
        }
    }
    private static bool HasDraft202012Marker(JsonElement schema)
    {
        return schema.TryGetProperty("$schema", out var marker) &&
               marker.ValueKind == JsonValueKind.String &&
               marker.GetString() == "https://json-schema.org/draft/2020-12/schema";
    }

}