using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data;

namespace Mohist.Server.Agent.Grains;

public class AgentGrain : Grain, IAgentGrain
{
    private readonly IStateStore<Domain.Agent> _agentStore;
    private readonly AgentQuerier _querier;
    private readonly TimeProvider _timeProvider;
    private Domain.Agent? _agent;

    internal AgentGrain(
        Orleans.Runtime.IGrainContext context,
        Orleans.Runtime.IGrainRuntime runtime,
        IStateStore<Domain.Agent> agentStore,
        AgentQuerier querier,
        TimeProvider timeProvider)
        : base(context, runtime)
    {
        _agentStore = agentStore;
        _querier = querier;
        _timeProvider = timeProvider;
    }

    public AgentGrain(IStateStore<Domain.Agent> agentStore, AgentQuerier querier, TimeProvider timeProvider)
    {
        _agentStore = agentStore;
        _querier = querier;
        _timeProvider = timeProvider;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _agent = await _agentStore.LoadAsync(CurrentKey());
    }

    public async Task<AgentInfo> CreateAsync(AgentCreateData data)
    {
        var (projectId, agentId) = ParseKey();
        if (!string.Equals(projectId, data.ProjectId, StringComparison.Ordinal))
            throw new InvalidOperationException("Agent project does not match grain key");

        await EnsureNameAvailableAsync(projectId, data.Name, exceptAgentId: null);

        var now = _timeProvider.GetUtcNow();
        _agent = new Domain.Agent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = data.Name,
            Description = data.Description ?? string.Empty,
            Instructions = data.Instructions,
            AgentConfig = Clone(data.AgentConfig),
            Skills = data.Skills?.ToArray() ?? [],
            MaxConcurrentRuns = data.MaxConcurrentRuns,
            Status = AgentStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _agentStore.SaveAsync(CurrentKey(), _agent);
        return AgentQuerier.ToInfo(_agent);
    }

    public Task<AgentInfo?> ShowAsync() =>
        Task.FromResult(_agent is null ? null : AgentQuerier.ToInfo(_agent));

    public async Task<AgentInfo?> UpdateAsync(AgentUpdateData data)
    {
        if (_agent is null) return null;

        if (data.Name is not null && !string.Equals(data.Name, _agent.Name, StringComparison.Ordinal))
            await EnsureNameAvailableAsync(_agent.ProjectId, data.Name, _agent.Id);

        if (data.Fields.Contains(nameof(data.Name))) _agent.Name = data.Name!;
        if (data.Fields.Contains(nameof(data.Description))) _agent.Description = data.Description ?? string.Empty;
        if (data.Fields.Contains(nameof(data.Instructions))) _agent.Instructions = data.Instructions ?? string.Empty;
        if (data.Fields.Contains(nameof(data.AgentConfig))) _agent.AgentConfig = Clone(data.AgentConfig);
        if (data.Fields.Contains(nameof(data.Skills))) _agent.Skills = data.Skills?.ToArray() ?? [];
        if (data.Fields.Contains(nameof(data.MaxConcurrentRuns))) _agent.MaxConcurrentRuns = data.MaxConcurrentRuns;
        _agent.UpdatedAt = _timeProvider.GetUtcNow();

        await _agentStore.SaveAsync(CurrentKey(), _agent);
        return AgentQuerier.ToInfo(_agent);
    }

    public async Task<AgentInfo?> ArchiveAsync()
    {
        if (_agent is null) return null;
        _agent.Status = AgentStatus.Archived;
        _agent.UpdatedAt = _timeProvider.GetUtcNow();
        await _agentStore.SaveAsync(CurrentKey(), _agent);
        return AgentQuerier.ToInfo(_agent);
    }

    public async Task<AgentInfo?> UnarchiveAsync()
    {
        if (_agent is null) return null;
        if (_agent.Status == AgentStatus.Active) return AgentQuerier.ToInfo(_agent);
        _agent.Status = AgentStatus.Active;
        _agent.UpdatedAt = _timeProvider.GetUtcNow();
        await _agentStore.SaveAsync(CurrentKey(), _agent);
        return AgentQuerier.ToInfo(_agent);
    }

    private async Task EnsureNameAvailableAsync(string projectId, string name, string? exceptAgentId)
    {
        var existing = await _querier.GetByNameAsync(projectId, name);
        if (existing is not null && !string.Equals(existing.Id, exceptAgentId, StringComparison.Ordinal))
            throw new AgentNameConflictException(projectId, name);
    }

    private (string ProjectId, string AgentId) ParseKey()
    {
        var key = CurrentKey();
        var parts = key.Split(':', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new InvalidOperationException($"Invalid Agent grain key '{key}'");
        return (parts[0], parts[1]);
    }

    private static System.Text.Json.JsonElement? Clone(System.Text.Json.JsonElement? value) =>
        value is null ? null : value.Value.Clone();

    private string CurrentKey() => GrainKey;
}
