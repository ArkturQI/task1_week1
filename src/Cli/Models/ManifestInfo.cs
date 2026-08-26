namespace Cli.Models;

internal sealed class ManifestInfo
{
    public string Module { get; init; } = "";
    public string Action { get; init; } = "";
    public int Version { get; init; }
    public string Hash { get; init; } = "";
    public string Content { get; init; } = "";
    public bool Enabled { get; init; }
    public bool IsDefault { get; init; }
    public int ManifestSize { get; init; }
    public string HttpMethod { get; init; } = "POST";
    public string TargetSchema { get; init; } = "";
    public string TargetFunction { get; init; } = "";
    public string Outcomes { get; init; } = "[]";
}