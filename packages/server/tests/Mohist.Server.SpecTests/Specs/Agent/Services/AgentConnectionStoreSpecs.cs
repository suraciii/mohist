using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

public sealed class AgentConnectionStoreSpecs : IAsyncLifetime
{
    private readonly FakeTimeProvider _timeProvider;
    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private FakeSecretStore _secretStore = null!;
    private AgentConnectionStore _store = null!;

    public AgentConnectionStoreSpecs()
    {
        _timeProvider = new FakeTimeProvider(TestTime.UtcNow);
    }

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        var querier = new AgentQuerier(_factory);
        _secretStore = new FakeSecretStore();
        _store = new AgentConnectionStore(
            _factory,
            querier,
            _secretStore,
            Array.Empty<IAgentConnectionProviderCleanup>(),
            _timeProvider);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreateBindsOneActiveAgentAndWorkspaceImmutably()
    {
        await SeedAgentAsync("proj-1", "agent-1", AgentStatus.Active);

        var connection = await _store.CreateAsync(NewConnection("proj-1", "agent-1", "team-1"));

        Assert.Equal(ConnectionProviderKind.Slack, connection.ProviderKind);
        Assert.Equal("team-1", connection.WorkspaceTeamId);
        Assert.Equal("agent-1", connection.AgentId);
        Assert.Equal("proj-1", connection.ProjectId);
        Assert.Equal(SetupProgressKind.CreateAppCredentials, connection.SetupProgress);
        Assert.Equal(DesiredStateKind.Enabled, connection.DesiredState);
        Assert.Equal(ConnectionHealthKind.Healthy, connection.ConnectionHealth);
        Assert.Equal(AgentReadinessKind.Unknown, connection.AgentReadiness);
    }

    [Fact]
    public async Task ArchivedAgentIsRejected()
    {
        await SeedAgentAsync("proj-2", "agent-archived", AgentStatus.Archived);

        var ex = await Assert.ThrowsAsync<AgentConnectionValidationException>(
            () => _store.CreateAsync(NewConnection("proj-2", "agent-archived", "team-1")));

        Assert.Equal("agent_archived", ex.Code);
    }

    [Fact]
    public async Task MissingAgentIsRejected()
    {
        var ex = await Assert.ThrowsAsync<AgentConnectionValidationException>(
            () => _store.CreateAsync(NewConnection("proj-3", "agent-missing", "team-1")));

        Assert.Equal("agent_not_found", ex.Code);
    }

    [Fact]
    public async Task DuplicateConnectionForSameAgentAndWorkspaceIsRefused()
    {
        await SeedAgentAsync("proj-4", "agent-4", AgentStatus.Active);
        await _store.CreateAsync(NewConnection("proj-4", "agent-4", "team-1"));

        var ex = await Assert.ThrowsAsync<AgentConnectionDuplicateException>(
            () => _store.CreateAsync(NewConnection("proj-4", "agent-4", "team-1")));

        Assert.Equal("proj-4", ex.ProjectId);
        Assert.Equal("agent-4", ex.AgentId);
        Assert.Equal("team-1", ex.WorkspaceTeamId);
    }

    [Fact]
    public async Task DistinctWorkspacesAllowCoexistence()
    {
        await SeedAgentAsync("proj-5", "agent-5", AgentStatus.Active);
        var a = await _store.CreateAsync(NewConnection("proj-5", "agent-5", "team-a"));
        var b = await _store.CreateAsync(NewConnection("proj-5", "agent-5", "team-b"));

        Assert.NotEqual(a.Id, b.Id);
        var list = await _store.ListAsync("proj-5");
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task FourFactsAreIndependent()
    {
        await SeedAgentAsync("proj-6", "agent-6", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-6", "agent-6", "team-1"));

        Assert.Equal(SetupProgressKind.CreateAppCredentials, created.SetupProgress);
        Assert.Equal(DesiredStateKind.Enabled, created.DesiredState);
        Assert.Equal(ConnectionHealthKind.Healthy, created.ConnectionHealth);
        Assert.Equal(AgentReadinessKind.Unknown, created.AgentReadiness);

        var updated = await _store.UpdateAsync(
            "proj-6", created.Id, new HashSet<string> { "setupProgress", "desiredState", "connectionHealth", "healthReason", "agentReadiness" },
            setupProgress: SetupProgressKind.Complete,
            desiredState: DesiredStateKind.Enabled,
            connectionHealth: ConnectionHealthKind.Degraded,
            healthReason: "backpressured",
            agentReadiness: AgentReadinessKind.Ready);

        Assert.NotNull(updated);
        Assert.Equal(SetupProgressKind.Complete, updated.SetupProgress);
        Assert.Equal(DesiredStateKind.Enabled, updated.DesiredState);
        Assert.Equal(ConnectionHealthKind.Degraded, updated.ConnectionHealth);
        Assert.Equal("backpressured", updated.HealthReason);
        Assert.Equal(AgentReadinessKind.Ready, updated.AgentReadiness);
    }

    [Fact]
    public async Task HealthyConnectionWithNotReadyAgentReadsAsSuch()
    {
        await SeedAgentAsync("proj-7", "agent-7", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-7", "agent-7", "team-1"));

        var updated = await _store.UpdateAsync(
            "proj-7", created.Id, new HashSet<string> { "setupProgress", "agentReadiness", "connectionHealth" },
            setupProgress: SetupProgressKind.Complete,
            agentReadiness: AgentReadinessKind.NeedsSetup,
            connectionHealth: ConnectionHealthKind.Healthy);

        Assert.NotNull(updated);
        Assert.Equal(SetupProgressKind.Complete, updated.SetupProgress);
        Assert.Equal(ConnectionHealthKind.Healthy, updated.ConnectionHealth);
        Assert.Equal(AgentReadinessKind.NeedsSetup, updated.AgentReadiness);
    }

    [Fact]
    public async Task ReadSurfaceDerivesReadinessIndependentlyFromConnectionHealth()
    {
        await SeedAgentAsync("proj-readiness", "agent-readiness", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-readiness", "agent-readiness", "team-1"));
        await _store.UpdateAsync(
            "proj-readiness", created.Id, new HashSet<string> { "setupProgress", "connectionHealth" },
            setupProgress: SetupProgressKind.Complete,
            connectionHealth: ConnectionHealthKind.Healthy);

        var displayed = await _store.GetAsync("proj-readiness", created.Id);

        Assert.NotNull(displayed);
        Assert.Equal(ConnectionHealthKind.Healthy, displayed.ConnectionHealth);
        Assert.Equal(AgentReadinessKind.NeedsSetup, displayed.AgentReadiness);
    }

    [Fact]
    public async Task GetReturnsSingleConnection()
    {
        await SeedAgentAsync("proj-8", "agent-8", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-8", "agent-8", "team-1"));

        var fetched = await _store.GetAsync("proj-8", created.Id);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.WorkspaceTeamId, fetched.WorkspaceTeamId);
    }

    [Fact]
    public async Task GetReturnsNullForMissingId()
    {
        var result = await _store.GetAsync("proj-9", "nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListReturnsAllNonDeletedConnections()
    {
        await SeedAgentAsync("proj-10", "agent-10", AgentStatus.Active);
        await SeedAgentAsync("proj-10", "agent-10b", AgentStatus.Active);
        var a = await _store.CreateAsync(NewConnection("proj-10", "agent-10", "team-a"));
        var b = await _store.CreateAsync(NewConnection("proj-10", "agent-10b", "team-b"));

        var list = await _store.ListAsync("proj-10");

        Assert.Contains(list, c => c.Id == a.Id);
        Assert.Contains(list, c => c.Id == b.Id);
    }

    [Fact]
    public async Task AdapterDiscoveryReturnsOnlyEnabledConnectionsWithBothCredentials()
    {
        await SeedAgentAsync("proj-adapter", "agent-configured", AgentStatus.Active);
        await SeedAgentAsync("proj-adapter", "agent-draft", AgentStatus.Active);
        var configured = await _store.CreateAsync(NewConnection("proj-adapter", "agent-configured", "team-a"));
        await _store.CreateAsync(NewConnection("proj-adapter", "agent-draft", "team-b"));
        await _secretStore.StoreAsync(new SecretStoreAddress("proj-adapter", configured.Id, SecretKind.AppToken), [1]);
        await _secretStore.StoreAsync(new SecretStoreAddress("proj-adapter", configured.Id, SecretKind.BotToken), [2]);

        var connections = await _store.ListForAdapterAsync();

        Assert.Equal([new SlackAdapterConnection("proj-adapter", configured.Id)], connections);
    }

    [Fact]
    public async Task ListRespectsIncludeDeleted()
    {
        await SeedAgentAsync("proj-11", "agent-11", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-11", "agent-11", "team-1"));
        await _store.DeleteAsync("proj-11", created.Id);

        var withoutDeleted = await _store.ListAsync("proj-11");
        Assert.DoesNotContain(withoutDeleted, c => c.Id == created.Id);

        var withDeleted = await _store.ListAsync("proj-11", includeDeleted: true);
        Assert.Contains(withDeleted, c => c.Id == created.Id);
    }

    [Fact]
    public async Task DeleteSoftDeletesAndPreservesAgent()
    {
        await SeedAgentAsync("proj-12", "agent-12", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-12", "agent-12", "team-1"));

        var deleted = await _store.DeleteAsync("proj-12", created.Id);

        Assert.NotNull(deleted);
        Assert.NotNull(deleted.DeletedAt);

        var fetched = await _store.GetAsync("proj-12", created.Id);
        Assert.NotNull(fetched);
        Assert.NotNull(fetched.DeletedAt);
    }

    [Fact]
    public async Task DeleteIsIdempotent()
    {
        await SeedAgentAsync("proj-13", "agent-13", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-13", "agent-13", "team-1"));

        var first = await _store.DeleteAsync("proj-13", created.Id);
        var second = await _store.DeleteAsync("proj-13", created.Id);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(first.DeletedAt);
        Assert.NotNull(second.DeletedAt);
    }

    [Fact]
    public async Task UpdateMutatesOnlySpecifiedFields()
    {
        await SeedAgentAsync("proj-14", "agent-14", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-14", "agent-14", "team-1"));

        var updated = await _store.UpdateAsync(
            "proj-14", created.Id, new HashSet<string> { "botName" },
            botName: "New Bot Name");

        Assert.NotNull(updated);
        Assert.Equal("New Bot Name", updated.BotName);
        Assert.Equal(created.SetupProgress, updated.SetupProgress);
        Assert.Equal(created.ConnectionHealth, updated.ConnectionHealth);
        Assert.Equal(created.AgentReadiness, updated.AgentReadiness);
    }

    [Fact]
    public async Task UpdateReturnsNullForMissingId()
    {
        var result = await _store.UpdateAsync(
            "proj-15", "nonexistent", new HashSet<string> { "botName" },
            botName: "name");
        Assert.Null(result);
    }

    [Fact]
    public async Task OwnerSlackUserIdIsSetByClaim()
    {
        await SeedAgentAsync("proj-16", "agent-16", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-16", "agent-16", "team-1"));

        var updated = await _store.UpdateAsync(
            "proj-16", created.Id, new HashSet<string> { "ownerSlackUserId" },
            ownerSlackUserId: "U12345");

        Assert.NotNull(updated);
        Assert.Equal("U12345", updated.OwnerSlackUserId);
    }

    [Fact]
    public async Task RebindingIsRejectedAndOriginalBindingIsUnchanged()
    {
        await SeedAgentAsync("proj-17", "agent-17", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-17", "agent-17", "team-1"));

        var exception = await Assert.ThrowsAsync<AgentConnectionValidationException>(
            () => _store.UpdateAsync("proj-17", created.Id, new HashSet<string> { "workspaceTeamId" }));

        Assert.Equal("immutable_binding", exception.Code);
        var persisted = await _store.GetAsync("proj-17", created.Id);
        Assert.NotNull(persisted);
        Assert.Equal("agent-17", persisted.AgentId);
        Assert.Equal("team-1", persisted.WorkspaceTeamId);
        Assert.Equal(created.AppId, persisted.AppId);
        Assert.Equal(created.BotUserId, persisted.BotUserId);
    }

    [Fact]
    public async Task UnboundConnectionCanBindSlackIdentityOnlyOnce()
    {
        await SeedAgentAsync("proj-17b", "agent-17b", AgentStatus.Active);
        var created = await _store.CreateAsync(new AgentConnection
        {
            Id = "conn-unbound",
            ProjectId = "proj-17b",
            AgentId = "agent-17b",
            ProviderKind = ConnectionProviderKind.Slack,
        });

        var bound = await _store.BindSlackIdentityAsync("proj-17b", created.Id, "team-17b", "app-17b", "bot-17b", "Mohist");
        var exception = await Assert.ThrowsAsync<AgentConnectionValidationException>(
            () => _store.BindSlackIdentityAsync("proj-17b", created.Id, "team-other", "app-other", "bot-other", "Other"));

        Assert.NotNull(bound);
        Assert.Equal("team-17b", bound.WorkspaceTeamId);
        Assert.Equal("immutable_binding", exception.Code);
    }

    [Fact]
    public async Task ReadyAgentWithUnreachableSlackReadsAsSuch()
    {
        await SeedAgentAsync("proj-18", "agent-18", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-18", "agent-18", "team-1"));

        var updated = await _store.UpdateAsync(
            "proj-18", created.Id, new HashSet<string> { "setupProgress", "connectionHealth", "agentReadiness" },
            setupProgress: SetupProgressKind.Complete,
            connectionHealth: ConnectionHealthKind.Unhealthy,
            agentReadiness: AgentReadinessKind.Ready);

        Assert.NotNull(updated);
        Assert.Equal(SetupProgressKind.Complete, updated.SetupProgress);
        Assert.Equal(ConnectionHealthKind.Unhealthy, updated.ConnectionHealth);
        Assert.Equal(AgentReadinessKind.Ready, updated.AgentReadiness);
    }

    [Fact]
    public async Task DeleteRemovesCredentialsAndPreservesAgent()
    {
        await SeedAgentAsync("proj-19", "agent-19", AgentStatus.Active);
        var created = await _store.CreateAsync(NewConnection("proj-19", "agent-19", "team-1"));
        var appToken = new SecretStoreAddress("proj-19", created.Id, SecretKind.AppToken);
        var botToken = new SecretStoreAddress("proj-19", created.Id, SecretKind.BotToken);
        await _secretStore.StoreAsync(appToken, [1]);
        await _secretStore.StoreAsync(botToken, [2]);

        await _store.DeleteAsync("proj-19", created.Id);

        Assert.Null(await _secretStore.LoadAsync(appToken));
        Assert.Null(await _secretStore.LoadAsync(botToken));
        var agent = await new AgentQuerier(_factory).GetByIdAsync("proj-19", "agent-19");
        Assert.NotNull(agent);
        Assert.Equal(AgentStatus.Active, agent.Status);
    }

    [Fact]
    public void ConnectionDoesNotStoreAgentExecutionDefinition()
    {
        var propertyNames = typeof(AgentConnection).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.DoesNotContain("Instructions", propertyNames);
        Assert.DoesNotContain("Runtime", propertyNames);
        Assert.DoesNotContain("Model", propertyNames);
        Assert.DoesNotContain("Variant", propertyNames);
        Assert.DoesNotContain("Skills", propertyNames);
        Assert.DoesNotContain("Concurrency", propertyNames);
    }

    private async Task SeedAgentAsync(string projectId, string agentId, string status)
    {
        await using var db = _factory.CreateDbContext();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId, ProjectId = projectId, Name = agentId, Status = status,
            }, JSON.Options),
        });
        await db.SaveChangesAsync();
    }

    private static AgentConnection NewConnection(string projectId, string agentId, string teamId) => new()
    {
        Id = $"conn_{Guid.NewGuid():N}",
        ProjectId = projectId,
        AgentId = agentId,
        ProviderKind = ConnectionProviderKind.Slack,
        WorkspaceTeamId = teamId,
        AppId = $"A{ Guid.NewGuid():N}",
        BotUserId = $"B{Guid.NewGuid():N}",
        BotName = "Test Bot",
    };

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _values = [];

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            _values[address] = plaintext;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.GetValueOrDefault(address));

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.Remove(address));

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
