using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cli.Models;

public sealed class WorkflowMap
{
    [JsonPropertyName("contract_version")]
    public string ContractVersion { get; set; } = "";

    [JsonPropertyName("flow_name")]
    public string FlowName { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("start_step")]
    public string StartStep { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<WorkflowStep> Steps { get; set; } = [];

    [JsonPropertyName("transitions")]
    public List<WorkflowTransition> Transitions { get; set; } = [];
}

public sealed class WorkflowStep
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("task")]
    public WorkflowTask? Task { get; set; }

    [JsonPropertyName("signal_type")]
    public string? SignalType { get; set; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("allowed_outcomes")]
    public List<string>? AllowedOutcomes { get; set; }

    [JsonIgnore]
    public JsonElement Raw { get; set; }
}

public sealed class WorkflowTask
{
    [JsonPropertyName("service")]
    public string Service { get; set; } = "";

    [JsonPropertyName("module")]
    public string Module { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("action_version")]
    public int ActionVersion { get; set; }

    [JsonPropertyName("required_policy")]
    public List<string> RequiredPolicy { get; set; } = [];

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; }

    [JsonPropertyName("retry")]
    public WorkflowRetry Retry { get; set; } = new();

    [JsonPropertyName("input_mapping")]
    public Dictionary<string, string> InputMapping { get; set; } = [];

    [JsonPropertyName("input_constants")]
    public Dictionary<string, JsonElement> InputConstants { get; set; } = [];
}

public sealed class WorkflowRetry
{
    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; set; }

    [JsonPropertyName("delays_ms")]
    public List<int> DelaysMs { get; set; } = [];
}

public sealed class WorkflowTransition
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";
}