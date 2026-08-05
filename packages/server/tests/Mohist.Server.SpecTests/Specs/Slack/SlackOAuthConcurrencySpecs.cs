using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackOAuthConcurrencySpecs : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(TestTime.UtcNow);
    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        var now = _time.GetUtcNow();
        await using var db = _factory.CreateDbContext();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = "enrollment-oauth-concurrency",
            WorkspaceTeamId = "T_OAUTH_CONCURRENCY",
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            ManagerCredentialRef = "manager-credential-ref",
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = "connection-oauth-concurrency",
            ProjectId = "project-oauth-concurrency",
            AgentId = "agent-oauth-concurrency",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T_OAUTH_CONCURRENCY",
            SetupProgress = SetupProgressKind.CreateAppCredentials,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Unknown,
            AccessPolicy = AccessPolicyKind.OwnerOnly,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = "child-oauth-concurrency",
            EnrollmentId = "enrollment-oauth-concurrency",
            WorkspaceTeamId = "T_OAUTH_CONCURRENCY",
            AgentConnectionId = "connection-oauth-concurrency",
            AppId = "A_OAUTH_CONCURRENCY",
            AppLifecycle = SlackAppLifecycle.Created,
            Authorization = SlackAuthorizationState.NotStarted,
            DesiredManifestVersion = 2,
            DesiredManifestHash = "desired",
            VerifiedScopesJson = "[]",
            ClientSecretRef = "client-secret-ref",
            SigningSecretRef = "signing-secret-ref",
            AppLevelTokenRef = "app-token-ref",
            BindingState = SlackAgentAppBindingState.Pending,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_consumers_accept_exactly_once()
    {
        var issuer = new SlackOAuthStateService(_factory, _time);
        var issued = await issuer.IssueAsync(
            "child-oauth-concurrency", "T_OAUTH_CONCURRENCY", "A_OAUTH_CONCURRENCY");
        var first = new SlackOAuthStateService(_factory, _time);
        var second = new SlackOAuthStateService(_factory, _time);

        var results = await Task.WhenAll(
            first.ConsumeAsync(issued.State, "child-oauth-concurrency", "T_OAUTH_CONCURRENCY", "A_OAUTH_CONCURRENCY"),
            second.ConsumeAsync(issued.State, "child-oauth-concurrency", "T_OAUTH_CONCURRENCY", "A_OAUTH_CONCURRENCY"));

        Assert.Equal(1, results.Count(result => result == SlackOAuthStateValidation.Accepted));
        Assert.Equal(1, results.Count(result => result == SlackOAuthStateValidation.ReplayAccepted));
        await using var db = _factory.CreateDbContext();
        var row = await db.SlackOAuthStates.SingleAsync();
        Assert.Equal(SlackOAuthStateOutcome.Accepted, row.Outcome);
        Assert.NotNull(row.ConsumedAt);
    }
}
