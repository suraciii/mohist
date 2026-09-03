using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.Slack.Services;
using Mohist.Server.TestSupport;

namespace Mohist.Server.Tests.Support;

public static class SlackAdmissionTestFactory
{
    public static SlackAdmissionTestContext Create()
    {
        var keeper = new SqliteConnection($"Data Source=admission-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        keeper.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(keeper)
            .Options;
        SqliteSchemaTemplate.CopyModelSchemaTo(keeper);
        var factory = new AdmissionDbContextFactory(options);
        var time = new FakeTimeProvider(TestTime.UtcNow);
        var projectId = $"project_{Guid.NewGuid():N}";
        using (var db = factory.CreateDbContext())
        {
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            });
            db.AgentConnections.Add(new AgentConnectionRow
            {
                Id = "connection-1",
                ProjectId = projectId,
                AgentId = "agent-1",
                WorkspaceTeamId = "T1",
                SetupProgress = SetupProgressKind.Complete,
                DesiredState = DesiredStateKind.Enabled,
                ConnectionHealth = ConnectionHealthKind.Healthy,
                LastHeartbeatAt = time.GetUtcNow(),
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            });
            db.SaveChanges();
        }

        var jobs = new AgentJobQuerier(factory, time);
        var defaults = new ProjectDefaultExecutionConfigReader(factory);
        var readiness = new AgentReadinessService(jobs, defaults);
        var secrets = new EmptySecretStore();
        var agents = new AgentQuerier(factory);
        var connections = new AgentConnectionStore(factory, agents, secrets, [], time);
        var verifier = new SlackSetupVerifier(new FakeSlackBotIdentityVerificationPort(), secrets, connections, time);
        var outbox = new SlackOutboxStore(
            factory,
            new NoopBackpressurer(),
            time,
            Options.Create(new SlackProviderOptions()));
        return new SlackAdmissionTestContext(
            keeper,
            factory,
            projectId,
            time,
            new SlackAdmissionService(readiness, outbox, verifier));
    }

    private sealed class AdmissionDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(false);
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class NoopBackpressurer : ISlackConnectionHealthBackpressurer
    {
        public Task FlipBackpressuredAsync(string projectId, string connectionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> RecoverBackpressuredAsync(string projectId, string connectionId, CancellationToken ct = default) => Task.FromResult(0);
    }
}

public sealed class SlackAdmissionTestContext(
    SqliteConnection keeper,
    IDbContextFactory<MohistDbContext> factory,
    string projectId,
    FakeTimeProvider time,
    SlackAdmissionService service) : IAsyncDisposable
{
    public string ProjectId => projectId;
    public IDbContextFactory<MohistDbContext> Factory => factory;
    public SlackAdmissionService Service => service;

    public AgentConnection Connection(string health) => new()
    {
        Id = "connection-1",
        ProjectId = projectId,
        AgentId = "agent-1",
        WorkspaceTeamId = "T1",
        SetupProgress = SetupProgressKind.Complete,
        DesiredState = DesiredStateKind.Enabled,
        ConnectionHealth = health,
        LastHeartbeatAt = time.GetUtcNow(),
    };

    public AgentInfo Agent(bool configured) => new(
        "agent-1",
        projectId,
        "Agent",
        string.Empty,
        "Instructions",
        configured ? JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }) : null,
        [],
        null,
        AgentStatus.Active,
        "2026-01-01T00:00:00Z",
        "2026-01-01T00:00:00Z");

    public async ValueTask DisposeAsync() => await keeper.DisposeAsync();
}
