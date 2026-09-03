using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.L1Tests.Support;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.L1Tests.Specs.Slack;

[Trait("level", "L1")]
public sealed class SlackManagerManagementBridgeSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerManagementBridgeSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Approved_operations_use_exact_envelopes_and_return_authoritative_results()
    {
        var seeded = await SeedAsync();
        var grant = await IssueGrantAsync(seeded);

        var list = await SendAsync(grant, new { operation = "list", args = new { } });
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("confirmed_state", (await DataAsync(list)).GetProperty("outcome").GetString());

        var diagnostics = await SendAsync(grant, new { operation = "diagnostics", args = new { } });
        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);

        var view = await SendAsync(grant, new
        {
            operation = "view",
            args = new { projectId = seeded.ProjectId, targetKind = "agent", targetId = seeded.AgentId },
        });
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal(seeded.AgentId, (await DataAsync(view)).GetProperty("state").GetProperty("id").GetString());

        var create = await SendAsync(grant, new
        {
            operation = "create",
            args = new { projectId = seeded.ProjectId, agentId = seeded.AgentId, accessPolicy = "owner_only" },
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var edit = await SendAsync(grant, new
        {
            operation = "edit",
            args = new { projectId = seeded.ProjectId, connectionId = seeded.ConnectionId, accessPolicy = "allowlist" },
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("access_policy_updated", (await DataAsync(edit)).GetProperty("code").GetString());

        var disable = await SendAsync(grant, new
        {
            operation = "disable",
            args = new { projectId = seeded.ProjectId, connectionId = seeded.ConnectionId },
        });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal(DesiredStateKind.Disabled,
            (await DataAsync(disable)).GetProperty("state").GetProperty("desiredState").GetString());

        var enable = await SendAsync(grant, new
        {
            operation = "enable",
            args = new { projectId = seeded.ProjectId, connectionId = seeded.ConnectionId },
        });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(DesiredStateKind.Enabled,
            (await DataAsync(enable)).GetProperty("state").GetProperty("desiredState").GetString());

        var claim = await SendAsync(grant, new
        {
            operation = "claim-owner",
            args = new { projectId = seeded.ProjectId, connectionId = seeded.ClaimConnectionId },
        });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var claimData = await DataAsync(claim);
        Assert.False(claimData.GetProperty("state").TryGetProperty("code", out _));
        Assert.True(claimData.GetProperty("state").TryGetProperty("expiresAt", out _));

        var transfer = await SendAsync(grant, new
        {
            operation = "transfer-owner",
            args = new { projectId = seeded.ProjectId, connectionId = seeded.TransferConnectionId },
        });
        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
    }

    [Theory]
    [InlineData("remove-binding")]
    [InlineData("permanent-delete")]
    [InlineData("delete")]
    [InlineData("run-sql")]
    [InlineData("mo slack message send")]
    public async Task Unknown_protected_and_reply_operations_are_rejected_without_mutation(string operation)
    {
        var seeded = await SeedAsync();
        var grant = await IssueGrantAsync(seeded);
        var before = await ConnectionStateAsync(seeded.ConnectionId);

        using var response = await SendAsync(grant, new { operation, args = new { } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("manager_operation_not_available", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, await ConnectionStateAsync(seeded.ConnectionId));
    }

    [Fact]
    public async Task Extra_properties_and_client_authority_overrides_are_rejected_before_service_invocation()
    {
        var seeded = await SeedAsync();
        var grant = await IssueGrantAsync(seeded);

        using var response = await SendAsync(grant, new
        {
            operation = "disable",
            args = new
            {
                projectId = seeded.ProjectId,
                connectionId = seeded.ConnectionId,
                workspaceTeamId = "T_FOREIGN",
                actor = "spoofed",
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("manager_arguments_invalid", document.RootElement.GetProperty("code").GetString());
    }

    private async Task<Seeded> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-manager-bridge-{suffix}";
        var workspace = $"T_MANAGER_BRIDGE_{suffix}";
        var enrollmentId = $"enrollment-manager-bridge-{suffix}";
        var agentId = $"agent-manager-bridge-{suffix}";
        var claimAgentId = $"agent-manager-claim-agent-{suffix}";
        var transferAgentId = $"agent-manager-transfer-agent-{suffix}";
        var connectionId = $"connection-manager-bridge-{suffix}";
        var claimConnectionId = $"connection-manager-claim-{suffix}";
        var transferConnectionId = $"connection-manager-transfer-{suffix}";
        var now = _fixture.TimeProvider.GetUtcNow();
        var agent = new DomainAgent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = "bridge-agent",
            Description = "Manager bridge test agent",
            Instructions = "Test instructions",
            Status = AgentStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow { Id = projectId, Name = projectId, CreatedAt = now, UpdatedAt = now });
        db.Agents.Add(new AgentRow { Id = agentId, ProjectId = projectId, Status = AgentStatus.Active, State = AgentStore.Serialize(agent) });
        db.Agents.Add(new AgentRow { Id = claimAgentId, ProjectId = projectId, Status = AgentStatus.Active, State = AgentStore.Serialize(new DomainAgent
        {
            Id = claimAgentId, ProjectId = projectId, Name = "claim-agent", Description = agent.Description,
            Instructions = agent.Instructions, Status = AgentStatus.Active, CreatedAt = now, UpdatedAt = now,
        }) });
        db.Agents.Add(new AgentRow { Id = transferAgentId, ProjectId = projectId, Status = AgentStatus.Active, State = AgentStore.Serialize(new DomainAgent
        {
            Id = transferAgentId, ProjectId = projectId, Name = "transfer-agent", Description = agent.Description,
            Instructions = agent.Instructions, Status = AgentStatus.Active, CreatedAt = now, UpdatedAt = now,
        }) });
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = enrollmentId,
            WorkspaceTeamId = workspace,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            ManagerReadiness = SlackManagerReadiness.Ready,
            ManagerActorId = "manager-actor",
            ClaimedSlackUserId = "U_MANAGER_BRIDGE",
            ManagerAppId = "A_MANAGER_BRIDGE",
            ManagerBotUserId = "U_MANAGER_BOT_BRIDGE",
            ManagerCredentialRef = "manager-credential-ref",
            PlanCode = "unknown",
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        AddConnection(db, connectionId, projectId, agentId, workspace, now, owner: "U_MANAGER_BRIDGE");
        AddConnection(db, claimConnectionId, projectId, claimAgentId, workspace, now, owner: null, setupProgress: SetupProgressKind.ClaimOwner);
        AddConnection(db, transferConnectionId, projectId, transferAgentId, workspace, now, owner: "U_OTHER_OWNER");
        await db.SaveChangesAsync();
        return new(projectId, workspace, enrollmentId, agentId, connectionId, claimConnectionId, transferConnectionId);
    }

    private static void AddConnection(
        MohistDbContext db,
        string id,
        string projectId,
        string agentId,
        string workspace,
        DateTimeOffset now,
        string? owner,
        string setupProgress = SetupProgressKind.Complete) =>
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = workspace,
            AppId = $"A_{id}",
            BotUserId = $"U_{id}",
            BotName = "bridge-bot",
            SetupProgress = setupProgress,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = owner,
            AccessPolicy = AccessPolicyKind.OwnerOnly,
            CreatedAt = now,
            UpdatedAt = now,
        });

    private async Task<ManagerExecutionGrant> IssueGrantAsync(Seeded seeded)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<ManagerExecutionCapabilityIssuer>().Issue(
            new ManagerExecutionIssueRequest(
                $"manager:job-{Guid.NewGuid():N}:work:0",
                new ManagerExecutionOrigin(
                    seeded.Workspace,
                    $"D_MANAGER_BRIDGE_{Guid.NewGuid():N}",
                    "1710000000.000001",
                    "1710000000.000001",
                    "U_MANAGER_BRIDGE",
                    seeded.EnrollmentId,
                    $"session-manager-bridge-{Guid.NewGuid():N}",
                    "slack:manager-bridge:input"),
                new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
                TimeSpan.FromMinutes(5)));
    }

    private async Task<HttpResponseMessage> SendAsync(ManagerExecutionGrant grant, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/slack-manager/management")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.ManagementCredential);
        request.Headers.TryAddWithoutValidation("X-Mohist-Manager-Mode", "1");
        return await _fixture.Client.SendAsync(request);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<string> ConnectionStateAsync(string connectionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await db.AgentConnections.Where(row => row.Id == connectionId).Select(row => row.DesiredState).SingleAsync();
    }

    private sealed record Seeded(
        string ProjectId,
        string Workspace,
        string EnrollmentId,
        string AgentId,
        string ConnectionId,
        string ClaimConnectionId,
        string TransferConnectionId);
}
