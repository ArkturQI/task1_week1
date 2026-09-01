using System.Text.Json;

namespace Workflow.Worker.Execution;

public sealed class PayloadMapper
{
    public JsonDocument BuildPayload(
        JsonDocument processData,
        JsonDocument inputMapping,
        JsonDocument inputConstants)
    {
        var payload =
            new Dictionary<string, object?>(
                StringComparer.Ordinal);

        if (inputConstants.RootElement.ValueKind ==
            JsonValueKind.Object)
        {
            foreach (var property in
                     inputConstants.RootElement.EnumerateObject())
            {
                payload[property.Name] =
                    CloneElement(property.Value);
            }
        }

        if (inputMapping.RootElement.ValueKind ==
            JsonValueKind.Object)
        {
            foreach (var property in
                     inputMapping.RootElement.EnumerateObject())
            {
                var targetPointer =
                    property.Name;

                var sourcePointer =
                    property.Value.GetString();

                if (string.IsNullOrWhiteSpace(
                        sourcePointer))
                {
                    throw new InvalidOperationException(
                        $"input_mapping source for '{targetPointer}' is empty");
                }

                var source =
                    ResolveJsonPointer(
                        processData.RootElement,
                        sourcePointer);

                SetJsonPointer(
                    payload,
                    targetPointer,
                    CloneElement(source));
            }
        }

        return JsonSerializer.SerializeToDocument(
            payload);
    }

    private static JsonElement ResolveJsonPointer(
        JsonElement root,
        string pointer)
    {
        if (pointer == "/")
        {
            return root;
        }

        if (!pointer.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"invalid JSON Pointer: {pointer}");
        }

        var current = root;

        foreach (var rawToken in
                 pointer.Split('/')[1..])
        {
            var token =
                rawToken
                    .Replace("~1", "/")
                    .Replace("~0", "~");

            if (current.ValueKind ==
                JsonValueKind.Object)
            {
                if (!current.TryGetProperty(
                        token,
                        out current))
                {
                    throw new InvalidOperationException(
                        $"source JSON Pointer not found: {pointer}");
                }
            }
            else if (current.ValueKind ==
                     JsonValueKind.Array)
            {
                if (!int.TryParse(
                        token,
                        out var index) ||
                    index < 0 ||
                    index >= current.GetArrayLength())
                {
                    throw new InvalidOperationException(
                        $"source JSON Pointer not found: {pointer}");
                }

                current =
                    current[index];
            }
            else
            {
                throw new InvalidOperationException(
                    $"source JSON Pointer cannot traverse value: {pointer}");
            }
        }

        return current;
    }

    private static void SetJsonPointer(
        Dictionary<string, object?> root,
        string pointer,
        object? value)
    {
        if (!pointer.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"invalid target JSON Pointer: {pointer}");
        }

        var tokens =
            pointer
                .Split('/')[1..]
                .Select(
                    x => x
                        .Replace("~1", "/")
                        .Replace("~0", "~"))
                .ToArray();

        if (tokens.Length != 1)
        {
            throw new InvalidOperationException(
                $"nested target mappings are not supported by this worker payload builder: {pointer}");
        }

        root[tokens[0]] =
            value;
    }

    private static object CloneElement(
        JsonElement element)
    {
        return JsonSerializer.Deserialize<object>(
                   element.GetRawText())
               ?? new object();
    }
}