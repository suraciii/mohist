using System.Text.Json;
using Mohist.Server.Agent.Services;

namespace Mohist.Server.Agent.Grains;

public interface IAgentGrain : IGrainWithStringKey
{
    Task<AgentInfo> CreateAsync(AgentCreateData data);
    Task<AgentInfo?> AdoptTaskFirstAsync(string idempotencyKey, string requestFingerprint);
    Task<AgentInfo?> ShowAsync();
    Task<AgentInfo?> UpdateAsync(AgentUpdateData data);
    Task<AgentInfo?> ArchiveAsync();
    Task<AgentInfo?> UnarchiveAsync();
}

[GenerateSerializer]
public sealed record AgentCreateData(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string Name,
    [property: Id(2)] string? Description,
    [property: Id(3)] string Instructions,
    [property: Id(4)] JsonElement? AgentConfig,
    [property: Id(5)] IReadOnlyList<string>? Skills,
    [property: Id(6)] int? MaxConcurrentRuns,
    [property: Id(7)] IReadOnlyList<string>? AllowedSubagentAgentIds = null,
    [property: Id(8)] string? Avatar = null,
    [property: Id(9)] string? TaskFirstIdempotencyKey = null,
    [property: Id(10)] string? TaskFirstRequestFingerprint = null);

[GenerateSerializer]
public sealed record AgentUpdateData(
    [property: Id(0)] string? Name,
    [property: Id(1)] string? Description,
    [property: Id(2)] string? Instructions,
    [property: Id(3)] JsonElement? AgentConfig,
    [property: Id(4)] IReadOnlyList<string>? Skills,
    [property: Id(5)] int? MaxConcurrentRuns,
    [property: Id(6)] IReadOnlySet<string> Fields,
    [property: Id(7)] IReadOnlyList<string>? AllowedSubagentAgentIds = null,
    [property: Id(8)] string? Avatar = null);

public sealed class AgentAlreadyExistsException : InvalidOperationException
{
    public AgentAlreadyExistsException(string projectId, string agentId)
        : base($"Agent '{agentId}' already exists in project '{projectId}'.")
    {
        ProjectId = projectId;
        AgentId = agentId;
    }

    public string ProjectId { get; }
    public string AgentId { get; }
}

public sealed class AgentTaskIdempotencyConflictException : InvalidOperationException
{
    public AgentTaskIdempotencyConflictException(string projectId, string agentId)
        : base($"The Idempotency-Key identifies a different task-first Agent definition in project '{projectId}'.")
    {
        ProjectId = projectId;
        AgentId = agentId;
    }

    public string ProjectId { get; }
    public string AgentId { get; }
}

public sealed class AgentNameConflictException : InvalidOperationException
{
    public AgentNameConflictException(string projectId, string name)
        : base($"Agent name '{name}' is already used in project '{projectId}'")
    {
        ProjectId = projectId;
        Name = name;
    }

    public string ProjectId { get; }
    public string Name { get; }
}
