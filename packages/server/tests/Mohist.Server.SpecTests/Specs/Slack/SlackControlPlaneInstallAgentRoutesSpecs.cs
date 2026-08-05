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
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("SlackControlPlaneRoutes")]
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
            InstallPath(projectId), new { enrollmentId, agentId }));

        Assert.Equal("provide_credentials", first.GetProperty("nextAction").GetString());
        Assert.Equal(team, first.GetProperty("connection").GetProperty("workspaceTeamId").GetString());
        Assert.Equal(string.Empty, first.GetProperty("connection").GetProperty("appId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("agentApp").GetProperty("appId").GetString()));
        Assert.Equal(createsBefore + 1, _fixture.Apps.CreateCalls);

        var createsBeforeRerun = _fixture.Apps.CreateCalls;
        var rerun = await ReadDataAsync(await client.PostAsJsonAsync(
            InstallPath(projectId), new { enrollmentId, agentId }));

        Assert.Equal(first.GetProperty("connection").GetProperty("id").GetString(), rerun.GetProperty("connection").GetProperty("id").GetString());
        Assert.Equal(first.GetProperty("agentApp").GetProperty("id").GetString(), rerun.GetProperty("agentApp").GetProperty("id").GetString());
        Assert.Equal(createsBeforeRerun, _fixture.Apps.CreateCalls);
    }

    [Fact]
    public async Task Install_rejects_when_the_workspace_has_no_active_enrollment()
    {
        var (projectId, agentId, team, _) = UniqueIds();
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active);
        using var client = _fixture.CreateOperatorClient();

        using var response = await client.PostAsJsonAsync(
            InstallPath(projectId), new { enrollmentId = "enrollment_missing", agentId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("enrollment_required", await CodeAsync(response));
    }

    [Fact]
    public async Task Install_rejects_an_archived_agent()
    {
        var (projectId, agentId, team, enrollmentId) = UniqueIds();
        await SeedAsync(projectId, agentId, AgentStatus.Archived, team, enrollmentId);
        using var client = _fixture.CreateOperatorClient();

        using var response = await client.PostAsJsonAsync(
            InstallPath(projectId), new { enrollmentId, agentId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agent_archived", await CodeAsync(response));
    }

    [Theory]
    [InlineData("credentials")]
    [InlineData("validation")]
    public async Task Secret_install_routes_require_an_operator_token_and_loopback(string suffix)
    {
        var projectId = $"project-install-auth-{Guid.NewGuid():N}";
        await SeedProjectAsync(projectId);
        var path = $"/api/projects/{projectId}/slack-manager/install-agent/{suffix}";
        var body = suffix == "credentials"
            ? (object)new { agentAppId = "agent_app_auth", botToken = "xoxb", appLevelToken = "xapp" }
            : new { agentAppId = "agent_app_auth", helloAppId = "A_AUTH" };

        using var anonymous = _fixture.CreateUnauthenticatedClient();
        using var anonymousResponse = await anonymous.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Forbidden, anonymousResponse.StatusCode);
        Assert.Equal("operator_credential_required", await CodeAsync(anonymousResponse));

        using var loopback = _fixture.CreateOperatorClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Test-Remote-Address", "203.0.113.10");
        using var nonLoopback = await loopback.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, nonLoopback.StatusCode);
        Assert.Equal("loopback_required", await CodeAsync(nonLoopback));
    }

    [Fact]
    public async Task Provision_then_socket_validation_drives_to_ready_and_binds_the_connection_once()
    {
        var (projectId, agentId, team, enrollmentId) = UniqueIds();
        await SeedAsync(projectId, agentId, AgentStatus.Active, team, enrollmentId);
        using var client = _fixture.CreateOperatorClient();

        var installed = await ReadDataAsync(await client.PostAsJsonAsync(
            InstallPath(projectId), new { enrollmentId, agentId }));
        var agentAppId = installed.GetProperty("agentApp").GetProperty("id").GetString()!;
        var appId = installed.GetProperty("agentApp").GetProperty("appId").GetString()!;

        _fixture.BotIdentity.Result = new SlackBotIdentityVerificationResult(
            Verified: true,
            WorkspaceTeamId: team,
            BotUserId: "U_INSTALL_BOT",
            AppId: appId,
            GrantedScopes: new HashSet<string> { "chat:write", "users:read" });

        using var provisioned = await client.PostAsJsonAsync(
            CredentialsPath(projectId), new { agentAppId, botToken = "xoxb-live", appLevelToken = "xapp-live" });
        provisioned.EnsureSuccessStatusCode();
        Assert.DoesNotContain("xoxb-live", await provisioned.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var validated = await client.PostAsJsonAsync(
            ValidationPath(projectId), new { agentAppId, helloAppId = appId });
        validated.EnsureSuccessStatusCode();

        var ready = await ReadDataAsync(await client.PostAsJsonAsync(
            InstallPath(projectId), new { enrollmentId, agentId }));
        Assert.Equal("ready", ready.GetProperty("nextAction").GetString());
        Assert.Equal(appId, ready.GetProperty("connection").GetProperty("appId").GetString());
        Assert.Equal("U_INSTALL_BOT", ready.GetProperty("connection").GetProperty("botUserId").GetString());

        using var replay = await client.PostAsJsonAsync(
            ValidationPath(projectId), new { agentAppId, helloAppId = appId });
        replay.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Caller_supplied_credential_address_in_install_body_is_rejected()
    {
        var (projectId, _, _, _) = UniqueIds();
        await SeedProjectAsync(projectId);
        using var client = _fixture.CreateOperatorClient();

        using var response = await client.PostAsJsonAsync(
            CredentialsPath(projectId),
            new { agentAppId = "agent_app_addr", botToken = "xoxb", appLevelToken = "xapp", secretKind = "botToken" });

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
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
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
}
