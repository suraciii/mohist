using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Collection("SlackControlPlaneRoutes")]
[Trait("level", "L1")]
public sealed class SlackControlPlaneInstallAgentRoutesSpecs
{
    private readonly SlackControlPlaneRoutesFixture _fixture;

    public SlackControlPlaneInstallAgentRoutesSpecs(SlackControlPlaneRoutesFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Install_creates_a_team_fixed_connection_and_agent_app_and_rerun_is_idempotent()
    {
        var (projectId, agentId, team, enrollmentId) = UniqueIds();
        await SeedAsync(projectId, agentId, AgentStatus.Active, team, enrollmentId);
        using var client = _fixture.CreateOperatorClient();

        var createsBefore = _fixture.Apps.CreateCalls;
        var first = await ReadDataAsync(await client.PostAsJsonAsync(
            InstallPath(projectId), new { agentId }));

        Assert.Equal("approve_install", first.GetProperty("nextAction").GetString());
        Assert.False(first.GetProperty("connection").TryGetProperty("workspaceTeamId", out _));
        Assert.False(first.GetProperty("agentApp").TryGetProperty("id", out _));
        Assert.False(first.GetProperty("agentApp").TryGetProperty("appId", out _));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("agentApp").GetProperty("installUrl").GetString()));
        Assert.Equal(createsBefore + 1, _fixture.Apps.CreateCalls);

        var internalFacts = await ReadManagedAppFactsAsync(projectId, agentId);
        var detail = await ReadDataAsync(await client.GetAsync(
            $"/api/projects/{projectId}/slack-connections/{internalFacts.ConnectionId}"));
        var publicManagedApp = detail.GetProperty("managedApp");
        Assert.Equal("approve_install", publicManagedApp.GetProperty("nextAction").GetString());
        Assert.False(string.IsNullOrWhiteSpace(publicManagedApp.GetProperty("installUrl").GetString()));
        Assert.False(publicManagedApp.TryGetProperty("id", out _));
        Assert.False(publicManagedApp.TryGetProperty("enrollmentId", out _));
        Assert.False(publicManagedApp.TryGetProperty("workspaceTeamId", out _));
        Assert.False(publicManagedApp.TryGetProperty("appId", out _));

        var createsBeforeRerun = _fixture.Apps.CreateCalls;
        var rerun = await ReadDataAsync(await client.PostAsJsonAsync(
            InstallPath(projectId), new { agentId }));

        Assert.Equal(internalFacts.ConnectionId, rerun.GetProperty("connection").GetProperty("id").GetString());
        Assert.Equal(createsBeforeRerun, _fixture.Apps.CreateCalls);
    }

    [Fact]
    public async Task Install_rejects_when_multiple_active_workspaces_require_a_selection()
    {
        var (projectId, agentId, _, _) = UniqueIds();
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active);
        // Auto-selection resolves only a single eligible workspace; two active
        // enrollments must surface an explicit selection instead of mutating
        // the first record a list happens to return.
        await SeedActiveEnrollmentAsync($"enrollment_{Guid.NewGuid():N}", $"T_{Guid.NewGuid():N}");
        await SeedActiveEnrollmentAsync($"enrollment_{Guid.NewGuid():N}", $"T_{Guid.NewGuid():N}");
        using var client = _fixture.CreateOperatorClient();

        using var response = await client.PostAsJsonAsync(
            InstallPath(projectId), new { agentId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("workspace_selection_required", await CodeAsync(response));
    }

    [Fact]
    public async Task Install_targets_the_selected_workspace_instead_of_the_first_enrollment()
    {
        var (projectId, agentId, _, _) = UniqueIds();
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active);
        var selectedTeam = $"T_{Guid.NewGuid():N}";
        await SeedActiveEnrollmentAsync($"enrollment_{Guid.NewGuid():N}", selectedTeam);
        await SeedActiveEnrollmentAsync($"enrollment_{Guid.NewGuid():N}", $"T_{Guid.NewGuid():N}");
        using var client = _fixture.CreateOperatorClient();

        var installed = await ReadDataAsync(await client.PostAsJsonAsync(
            $"{InstallPath(projectId)}?workspaceTeamId={selectedTeam}", new { agentId }));

        Assert.Equal("approve_install", installed.GetProperty("nextAction").GetString());
        Assert.Equal(selectedTeam, await ReadConnectionTeamAsync(
            installed.GetProperty("connection").GetProperty("id").GetString()!));
    }

    [Fact]
    public async Task Install_rejects_an_archived_agent()
    {
        var (projectId, agentId, team, enrollmentId) = UniqueIds();
        await SeedAsync(projectId, agentId, AgentStatus.Archived, team, enrollmentId);
        using var client = _fixture.CreateOperatorClient();

        using var response = await client.PostAsJsonAsync(
            InstallPath(projectId), new { agentId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agent_archived", await CodeAsync(response));
    }

    [Fact]
    public async Task Credential_install_route_requires_an_operator_token_and_loopback()
    {
        var projectId = $"project-install-auth-{Guid.NewGuid():N}";
        await SeedProjectAsync(projectId);
        var path = CredentialsPath(projectId);
        var body = new { agentAppId = "agent_app_auth", botToken = "xoxb", appLevelToken = "xapp" };

        using var anonymous = _fixture.CreateUnauthenticatedClient();
        using var anonymousResponse = await anonymous.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(anonymousResponse.Headers.WwwAuthenticate).ToString());

        using var loopback = _fixture.CreateOperatorClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Test-Remote-Address", "203.0.113.10");
        using var nonLoopback = await loopback.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, nonLoopback.StatusCode);
        Assert.Equal("loopback_required", await CodeAsync(nonLoopback));
    }

    [Fact]
    public async Task The_socket_validation_bypass_route_is_removed_and_cannot_drive_to_ready()
    {
        var (projectId, agentId, team, enrollmentId) = UniqueIds();
        await SeedAsync(projectId, agentId, AgentStatus.Active, team, enrollmentId);
        using var client = _fixture.CreateOperatorClient();

        // Even an operator-authenticated, loopback request must not find the
        // bypass: the only Socket hello path is the validation lease.
        using var response = await client.PostAsJsonAsync(
            ValidationPath(projectId), new { agentAppId = "agent_app_gone", helloAppId = "A_GONE" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Provision_then_validation_lease_hello_drives_to_ready_and_binds_the_connection_once()
    {
        var (projectId, agentId, team, enrollmentId) = UniqueIds();
        await SeedAsync(projectId, agentId, AgentStatus.Active, team, enrollmentId);
        using var client = _fixture.CreateOperatorClient();

        var installed = await ReadDataAsync(await client.PostAsJsonAsync(
            InstallPath(projectId), new { agentId }));
        var internalFacts = await ReadManagedAppFactsAsync(projectId, agentId);
        var agentAppId = internalFacts.AgentAppId;
        var appId = internalFacts.AppId;
        var connectionId = installed.GetProperty("connection").GetProperty("id").GetString()!;

        _fixture.BotIdentity.Result = new SlackBotIdentityVerificationResult(
            Verified: true,
            WorkspaceTeamId: team,
            BotUserId: "U_INSTALL_BOT",
            AppId: appId,
            GrantedScopes: new HashSet<string>(SlackManifestDefinition.For(SlackManifestKind.AgentApp).BotScopes));

        using var provisioned = await client.PostAsJsonAsync(
            CredentialsPath(projectId), new { agentId, botToken = "xoxb-live", appLevelToken = "xapp-live" });
        provisioned.EnsureSuccessStatusCode();
        Assert.DoesNotContain("xoxb-live", await provisioned.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The only Socket hello path is the validation lease; the deleted
        // /install-agent/validation route can no longer reach ApplySocketValidation.
        var targetRef = new SlackLeaseTargetRef.Connection(projectId, connectionId);
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<SlackAdapterLeaseService>();
            var validation = await leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
            Assert.NotNull(validation);
            Assert.Equal(appId, validation!.ExpectedAppId);
            Assert.Equal(SlackHelloOutcome.Verified,
                await leases.ReportHelloAsync("operator-1", targetRef, validation.LeaseId, appId));
        }

        var ready = await ReadDataAsync(await client.PostAsJsonAsync(
            InstallPath(projectId), new { agentId }));
        Assert.Equal("ready", ready.GetProperty("nextAction").GetString());
        Assert.False(ready.GetProperty("connection").TryGetProperty("appId", out _));
        Assert.False(ready.GetProperty("connection").TryGetProperty("botUserId", out _));
    }

    [Fact]
    public async Task Caller_supplied_credential_address_in_install_body_is_rejected()
    {
        var (projectId, _, _, _) = UniqueIds();
        await SeedProjectAsync(projectId);
        using var client = _fixture.CreateOperatorClient();

        using var response = await client.PostAsJsonAsync(
            CredentialsPath(projectId),
            new { agentId = "agent_addr", agentAppId = "agent_app_addr", botToken = "xoxb", appLevelToken = "xapp", secretKind = "botToken" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("credential_address_not_supported", await CodeAsync(response));
    }

    private static string InstallPath(string projectId) =>
        $"/api/projects/{projectId}/slack-manager/install-agent";

    private static string CredentialsPath(string projectId) =>
        $"/api/projects/{projectId}/slack-manager/install-agent/credentials";

    private static string ValidationPath(string projectId) =>
        $"/api/projects/{projectId}/slack-manager/install-agent/validation";

    private static (string ProjectId, string AgentId, string Team, string EnrollmentId) UniqueIds() =>
        ($"project_{Guid.NewGuid():N}", $"agent_{Guid.NewGuid():N}", $"T_{Guid.NewGuid():N}", $"enrollment_{Guid.NewGuid():N}");

    private async Task SeedAsync(string projectId, string agentId, string agentStatus, string team, string enrollmentId)
    {
        await SeedAgentAsync(projectId, agentId, agentStatus);
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = enrollmentId,
            WorkspaceTeamId = team,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = $"connection_{agentId}",
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = team,
            SetupProgress = SetupProgressKind.CreateAppCredentials,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Unhealthy,
            HealthReason = "managed_app_not_ready",
            AgentReadiness = AgentReadinessKind.Ready,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedProjectAsync(string projectId)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow { Id = projectId, Name = projectId, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentAsync(string projectId, string agentId, string agentStatus)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow { Id = projectId, Name = projectId, CreatedAt = now, UpdatedAt = now });
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = "Install Agent",
            Status = agentStatus,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = "Install Agent",
                Status = agentStatus,
                Instructions = "Handle Slack setup.",
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedActiveEnrollmentAsync(string enrollmentId, string team)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = enrollmentId,
            WorkspaceTeamId = team,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private async Task<string> ReadConnectionTeamAsync(string connectionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var connection = await db.AgentConnections.SingleAsync(row => row.Id == connectionId);
        return connection.WorkspaceTeamId;
    }

    private async Task<(string ConnectionId, string AgentAppId, string AppId)> ReadManagedAppFactsAsync(
        string projectId,
        string agentId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var connection = await db.AgentConnections.SingleAsync(row =>
            row.ProjectId == projectId && row.AgentId == agentId && row.DeletedAt == null);
        var app = await db.ManagedSlackAgentApps.SingleAsync(row => row.AgentConnectionId == connection.Id);
        return (connection.Id, app.Id, app.AppId);
    }
}
