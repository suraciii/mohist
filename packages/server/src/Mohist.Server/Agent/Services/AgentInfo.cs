using System.Text.Json;

namespace Mohist.Server.Agent.Services;

public static class AgentOrigins
{
    public const string Project = "project";
    public const string BuiltIn = "built-in";
}

[GenerateSerializer]
public sealed record AgentEffectiveExecutionConfig(
    [property: Id(0)] string Runtime,
    [property: Id(1)] string? Model,
    [property: Id(2)] string? Variant);

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
    AgentExecutabilityResult? Executability = null,
    [property: Id(12)]
    IReadOnlyList<string>? AllowedSubagentAgentIds = null,
    [property: Id(13)]
    string? Avatar = null,
    [property: Id(14)]
    string? Purpose = null,
    [property: Id(15)]
    IReadOnlyList<string>? Permissions = null,
    [property: Id(16)]
    AgentEffectiveExecutionConfig? EffectiveExecutionConfig = null,
    /// <summary>
    /// <c>project</c> for a stored Project Agent, <c>built-in</c> for an
    /// unshadowed built-in Workflow Agent definition. Append-only Orleans
    /// field id (next free after <see cref="EffectiveExecutionConfig"/>).
    /// </summary>
    [property: Id(17)]
    string? Origin = null,
    /// <summary>
    /// True when this stored Project Agent carries the name of a built-in
    /// Workflow Agent and therefore shadows it. Append-only Orleans field id
    /// (next free after <see cref="Origin"/>).
    /// </summary>
    [property: Id(18)]
    bool OverridesBuiltIn = false);
