using Cli.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cli.Services;

internal static class WorkflowMapParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly IDeserializer YamlDeserializer =
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

    public static bool TryParse(
        string? file,
        out WorkflowMap? map,
        out string? error)
    {
        map = null;
        error = null;

        if (string.IsNullOrWhiteSpace(file))
        {
            error = "workflow map file is required";
            return false;
        }

        try
        {
            string content;

            if (file == "/dev/stdin")
            {
                content = Console.In.ReadToEnd();
            }
            else
            {
                if (!File.Exists(file))
                {
                    error = $"file not found: {file}";
                    return false;
                }

                content = File.ReadAllText(file);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                error = "workflow map is empty";
                return false;
            }

            var extension =
                Path.GetExtension(file)
                    .ToLowerInvariant();

            if (extension is ".yaml" or ".yml")
            {
                map = YamlDeserializer.Deserialize<WorkflowMap>(content);

                if (map is null)
                {
                    error = "workflow map is empty";
                    return false;
                }

                return true;
            }

            map = JsonSerializer.Deserialize<WorkflowMap>(
                content,
                JsonOptions);

            if (map is null)
            {
                error = "workflow map is empty";
                return false;
            }

            AttachRawElements(map, content);

            return true;
        }
        catch (Exception ex)
        {
            error = $"invalid workflow map: {ex.Message}";
            return false;
        }
    }

    private static void AttachRawElements(
        WorkflowMap map,
        string content)
    {
        using var document =
            JsonDocument.Parse(content);

        var root =
            document.RootElement;

        if (!root.TryGetProperty(
                "steps",
                out var steps) ||
            steps.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        for (var i = 0; i < map.Steps.Count; i++)
        {
            if (i < steps.GetArrayLength())
            {
                map.Steps[i].Raw =
                    steps[i].Clone();
            }
        }
    }
}