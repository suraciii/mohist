namespace Mohist.Server.Infrastructure;

/// <summary>
/// Agent-owned immutable execution definition produced by
/// <see cref="IAgentExecutionSnapshotResolver"/>. Carries the five fields
/// that determine how every launch and follow-up of this Agent is
/// executed (Instructions, Runtime, Model, Variant, ordered Skills).
/// Resolved once from the active Agent definition and stamped into the
/// durable AgentJob / routed-plan / generic-AgentSession settings so
/// subsequent Agent edits cannot alter in-flight or queued work.
///
/// <para>
/// <see cref="Runtime"/> defaults to <see cref="AgentConfigSchema.OpenCodeRuntime"/>
/// when the Agent's config omits <c>runtime</c>; an out-of-set value falls
/// back to the same default. <see cref="Skills"/> is the Agent's stored
/// ordered list (possibly empty). <see cref="Model"/> and
/// <see cref="Variant"/> are read from the Agent's validated config.
/// </para>
/// </summary>
[GenerateSerializer]
public sealed record AgentExecutionDefinition(
    [property: Id(0)] string Instructions,
    [property: Id(1)] string Runtime,
    [property: Id(2)] string? Model,
    [property: Id(3)] string? Variant,
    [property: Id(4)] IReadOnlyList<string> Skills,
    [property: Id(5)] AllowedSubagentSnapshot[]? AllowedSubagents = null);

[GenerateSerializer]
public sealed record AllowedSubagentSnapshot(
    [property: Id(0)] string AgentId,
    [property: Id(1)] string NameAtLaunch,
    [property: Id(2)] string DescriptionAtLaunch);

[GenerateSerializer]
public sealed record AgentSessionStartup(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string SessionId,
    [property: Id(2)] string? ParentSessionId,
    [property: Id(3)] AllowedSubagentSnapshot[] AllowedSubagents,
    [property: Id(4)] string SpawnCommand);

public interface IAgentExecutionSnapshotResolver
{
    Task<AgentExecutionDefinition?> ResolveAsync(string projectId, string agentRef);
}
