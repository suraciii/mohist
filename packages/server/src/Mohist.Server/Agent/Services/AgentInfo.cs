using System.Text.Json;

namespace Mohist.Server.Agent.Services;

[GenerateSerializer]
public sealed record AgentInfo(
    [property: Id(0)]
    string Id,
    [property: Id(1)]
    string ProjectId,
    [property: Id(2)]
    string Name,
    [property: Id(3)]
    string Description,
    [property: Id(4)]
    string Instructions,
    [property: Id(5)]
    JsonElement? AgentConfig,
    [property: Id(6)]
    IReadOnlyList<string> Skills,
    [property: Id(7)]
    int? MaxConcurrentRuns,
    [property: Id(8)]
    string Status,
    [property: Id(9)]
    string CreatedAt,
    [property: Id(10)]
    string UpdatedAt,
    [property: Id(11)]
    AgentReadinessResult? Readiness = null,
    [property: Id(12)]
    IReadOnlyList<string>? AllowedSubagentAgentIds = null,
    [property: Id(13)]
    string? Avatar = null,
    [property: Id(14)]
    string? Purpose = null,
    [property: Id(15)]
    IReadOnlyList<string>? Permissions = null);
