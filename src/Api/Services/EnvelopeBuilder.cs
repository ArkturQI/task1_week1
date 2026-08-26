using System.Text.Json;

namespace Api.Services;

public static class EnvelopeBuilder
{
    public static object Error(string? correlationId, int? actionVersion, string code, string message, int httpStatus)
    {
        var meta = new Dictionary<string, object?>();
        if (correlationId is not null) meta["correlationId"] = correlationId;
        meta["actionVersion"] = actionVersion;

        return new Dictionary<string, object?>
        {
            ["status"] = "error",
            ["code"] = code,
            ["message"] = message,
            ["retryable"] = httpStatus >= 500,
            ["details"] = new Dictionary<string, object>(),
            ["meta"] = meta
        };
    }

    public static object InjectCorrelationId(JsonElement root, string correlationId, int? actionVersion)
    {
        var result = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "meta")
            {
                var meta = new Dictionary<string, object?> { ["correlationId"] = correlationId };
                if (actionVersion is not null) meta["actionVersion"] = actionVersion;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                    foreach (var mp in prop.Value.EnumerateObject())
                        if (!meta.ContainsKey(mp.Name))
                            meta[mp.Name] = mp.Value.Clone();
                result["meta"] = meta;
            }
            else
            {
                result[prop.Name] = prop.Value.Clone();
            }
        }
        if (!result.ContainsKey("meta"))
            result["meta"] = new Dictionary<string, object?>
            {
                ["correlationId"] = correlationId,
                ["actionVersion"] = actionVersion
            };
        return result;
    }

    public static int MapHttpCode(string code) => code switch
    {
        "auth.invalid" => 401,
        "request.invalid" => 400,
        "idempotency.required" => 400,
        "access.denied" => 403,
        "action.not_found" => 404,
        "operation.not_found" => 404,
        "idempotency.conflict" => 409,
        "payload.invalid" => 422,
        "dependency.unavailable" => 503,
        "action.contract_violation" => 500,
        "action.timeout" => 504,
        _ => 500
    };
}