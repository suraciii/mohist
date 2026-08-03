using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackManagerApplicationSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerApplicationSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_selects_active_agent_persists_manager_records_and_is_idempotent()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Active);
        await SetupManagerAsync("T_MANAGER_CREATE");
        var request = new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_MANAGER_CREATE",
            accessPolicy = AccessPolicyKind.Allowlist,
            ownerSlackUserId = "U_OWNER",
            transportKind = "socket",
        };

        using var firstResponse = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), request);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await ReadDataAsync(firstResponse);
        var connectionId = first.GetProperty("connection").GetProperty("id").GetString()!;
        var childId = first.GetProperty("managedApp").GetProperty("id").GetString()!;
        Assert.Equal("release_helper", first.GetProperty("preview").GetProperty("botName").GetString());
        Assert.Equal("not_created", first.GetProperty("managedApp").GetProperty("appLifecycle").GetString());
        Assert.Equal("create_child_app", first.GetProperty("managedApp").GetProperty("nextAction").GetString());

        using var secondResponse = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), request);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await ReadDataAsync(secondResponse);
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(connectionId, second.GetProperty("connection").GetProperty("id").GetString());
        Assert.Equal(childId, second.GetProperty("managedApp").GetProperty("id").GetString());

        using var listResponse = await _fixture.Client.GetAsync(
            $"{ManagerPath(seeded.ProjectId, "/agents")}?workspaceTeamId=T_MANAGER_CREATE");
        listResponse.EnsureSuccessStatusCode();
        var list = await ReadDataAsync(listResponse);
        var option = Assert.Single(list.EnumerateArray());
        Assert.Equal(seeded.Agent.Id, option.GetProperty("agentId").GetString());
        Assert.Equal(childId, option.GetProperty("managedApp").GetProperty("id").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.SlackWorkspaceEnrollments.Where(row => row.WorkspaceTeamId == "T_MANAGER_CREATE").ToListAsync());
        var connection = await db.AgentConnections.SingleAsync(row => row.Id == connectionId);
        Assert.Equal(AccessPolicyKind.Allowlist, connection.AccessPolicy);
        Assert.Equal("T_MANAGER_CREATE", connection.WorkspaceTeamId);
        var child = await db.ManagedSlackChildApps.SingleAsync(row => row.Id == childId);
        Assert.Equal(connectionId, child.AgentConnectionId);
        Assert.NotEmpty(child.DesiredManifestHash);
        Assert.Equal(seeded.Agent.Id, (await db.Agents.SingleAsync(row => row.Id == seeded.Agent.Id)).Id);
    }

    [Fact]
    public async Task Archived_agent_is_not_a_manager_candidate_or_create_target()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Archived);
        using var response = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_MANAGER_ARCHIVED",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("agent_archived", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Authorized_manager_tool_creates_a_default_agent_before_mounting_it()
    {
        const string team = "T_MANAGER_DEFAULT_AGENT";
        const string appId = "A_MANAGER_DEFAULT_AGENT";
        const string owner = "U_MANAGER_DEFAULT_AGENT";
        var projectId = $"project_manager_default_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        using var setupResponse = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = appId,
            managerBotUserId = "U_MANAGER_BOT_DEFAULT_AGENT",
            managerCredentialRef = "manager-credential-default-agent",
        });
        setupResponse.EnsureSuccessStatusCode();
        var claimCode = (await ReadDataAsync(setupResponse)).GetProperty("claimCode").GetString()!;

        using var claimResponse = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/ingress", new
        {
            appId,
            workspaceTeamId = team,
            conversationId = "D_MANAGER_DEFAULT_AGENT",
            messageTs = "1710000000.000001",
            senderSlackUserId = owner,
            text = $"claim {claimCode}",
            isDirectMessage = true,
        });
        claimResponse.EnsureSuccessStatusCode();

        await using (var managerScope = _fixture.Services.CreateAsyncScope())
        {
            var access = managerScope.ServiceProvider.GetRequiredService<ManagerActorAccessDecider>();
            var actor = await access.AuthenticateAsync(team, owner);
            Assert.NotNull(actor.Actor);
            var result = await managerScope.ServiceProvider.GetRequiredService<SlackManagerToolExecutor>()
                .ExecuteAsync(actor.Actor!, new SlackManagerToolInvocation(
                    "create",
                    ProjectId: projectId,
                    AgentName: "release-helper",
                    DailyResponsibility: "review release changes"),
                    "manager-tool-default-agent");
            Assert.True(result.Succeeded, result.Message);
        }

        await using var verify = _fixture.Services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var agent = AgentStore.Deserialize(await database.Agents
            .Where(row => row.ProjectId == projectId && row.Name == "release-helper")
            .Select(row => row.State)
            .SingleAsync());
        Assert.NotNull(agent);
        Assert.Equal("opencode", agent!.AgentConfig!.Value.GetProperty("runtime").GetString());
        Assert.Contains("review release changes", agent.Instructions, StringComparison.Ordinal);
        Assert.NotNull(await database.AgentConnections.SingleOrDefaultAsync(row =>
            row.ProjectId == projectId
            && row.AgentId == agent.Id
            && row.WorkspaceTeamId == team));
    }

    [Fact]
    public async Task Remove_binding_keeps_child_facts_and_permanent_delete_requires_explicit_confirmation()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Active);
        await SetupManagerAsync("T_MANAGER_REMOVE");
        using var createResponse = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_MANAGER_REMOVE",
        });
        var created = await ReadDataAsync(createResponse);
        var connectionId = created.GetProperty("connection").GetProperty("id").GetString()!;
        var childId = created.GetProperty("managedApp").GetProperty("id").GetString()!;

        using var removeResponse = await _fixture.Client.PostAsJsonAsync(
            ManagerPath(seeded.ProjectId, $"/connections/{connectionId}/remove-binding"), new { });
        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
        var removed = await ReadDataAsync(removeResponse);
        Assert.True(removed.GetProperty("removedBinding").GetBoolean());
        Assert.Equal(childId, removed.GetProperty("managedApp").GetProperty("id").GetString());

        using var unconfirmedResponse = await _fixture.Client.PostAsJsonAsync(
            ManagerPath(seeded.ProjectId, $"/connections/{connectionId}/permanent-delete"), new { });
        Assert.Equal(HttpStatusCode.Conflict, unconfirmedResponse.StatusCode);
        using (var error = JsonDocument.Parse(await unconfirmedResponse.Content.ReadAsStringAsync()))
            Assert.Equal("confirmation_required", error.RootElement.GetProperty("code").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.NotNull(await db.AgentConnections.SingleAsync(row => row.Id == connectionId && row.DeletedAt != null));
        Assert.NotNull(await db.ManagedSlackChildApps.SingleAsync(row => row.Id == childId && row.DeletedAt == null));
        Assert.NotNull(await db.Agents.SingleAsync(row => row.Id == seeded.Agent.Id));
    }

    [Fact]
    public async Task Client_identity_fields_are_rejected_without_creating_or_auditing_an_actor()
    {
        var seeded = await SeedAgentAsync(AgentStatus.Active);
        using var createResponse = await _fixture.Client.PostAsJsonAsync(ManagerPath(seeded.ProjectId, "/apps"), new
        {
            agentId = seeded.Agent.Id,
            workspaceTeamId = "T_MANAGER_IDENTITY",
            managerExternalId = "spoofed-manager",
        });

        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
        using (var error = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
            Assert.Equal("client_identity_not_supported", error.RootElement.GetProperty("code").GetString());

        using var deleteResponse = await _fixture.Client.PostAsJsonAsync(
            ManagerPath(seeded.ProjectId, "/connections/unknown/permanent-delete"), new
            {
                confirmation = "DELETE",
                actor = "spoofed-actor",
            });

        Assert.Equal(HttpStatusCode.BadRequest, deleteResponse.StatusCode);
        using (var error = JsonDocument.Parse(await deleteResponse.Content.ReadAsStringAsync()))
            Assert.Equal("client_identity_not_supported", error.RootElement.GetProperty("code").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackWorkspaceEnrollments
            .Where(row => row.WorkspaceTeamId == "T_MANAGER_IDENTITY")
            .ToListAsync());
    }

    [Fact]
    public async Task Setup_and_manager_claim_ingress_are_durable_and_do_not_leak_credentials()
    {
        const string team = "T_MANAGER_S0_INGRESS";
        const string credentialRef = "manager-credential-s0-ingress";
        using var setupResponse = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = "A_MANAGER_S0_INGRESS",
            managerBotUserId = "U_MANAGER_S0_INGRESS",
            managerCredentialRef = credentialRef,
        });
        setupResponse.EnsureSuccessStatusCode();
        var setupJson = await setupResponse.Content.ReadAsStringAsync();
        var setup = await ReadDataAsync(setupResponse);
        Assert.DoesNotContain(credentialRef, setupJson, StringComparison.Ordinal);

        using var statusResponse = await _fixture.Client.GetAsync($"/api/slack-manager/status?workspaceTeamId={team}");
        statusResponse.EnsureSuccessStatusCode();
        Assert.DoesNotContain(credentialRef, await statusResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("claim_manager", (await ReadDataAsync(statusResponse)).GetProperty("nextAction").GetString());

        var claimCode = setup.GetProperty("claimCode").GetString()!;
        using var deniedIngress = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/ingress", new
        {
            appId = "A_MANAGER_S0_INGRESS",
            workspaceTeamId = team,
            conversationId = "D_MANAGER_S0",
            messageTs = "1710000000.000000",
            senderSlackUserId = "U_MANAGER_UNCLAIMED",
            text = "list agents",
            isDirectMessage = true,
            actor = "forged-actor",
        });
        Assert.Equal(HttpStatusCode.BadRequest, deniedIngress.StatusCode);
        using (var deniedError = JsonDocument.Parse(await deniedIngress.Content.ReadAsStringAsync()))
            Assert.Equal("client_identity_not_supported", deniedError.RootElement.GetProperty("code").GetString());

        using var unclaimedIngress = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/ingress", new
        {
            appId = "A_MANAGER_S0_INGRESS",
            workspaceTeamId = team,
            conversationId = "D_MANAGER_S0",
            messageTs = "1710000000.000000",
            senderSlackUserId = "U_MANAGER_UNCLAIMED",
            text = "list agents",
            isDirectMessage = true,
        });
        unclaimedIngress.EnsureSuccessStatusCode();
        var unclaimed = await ReadDataAsync(unclaimedIngress);
        Assert.Equal("rejected", unclaimed.GetProperty("decision").GetString());
        Assert.False(unclaimed.GetProperty("deliveryIntentCreated").GetBoolean());

        var message = new
        {
            appId = "A_MANAGER_S0_INGRESS",
            workspaceTeamId = team,
            conversationId = "D_MANAGER_S0",
            messageTs = "1710000000.000001",
            senderSlackUserId = "U_MANAGER_OWNER",
            text = $"claim {claimCode}",
            isDirectMessage = true,
        };
        using var firstIngress = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/ingress", message);
        firstIngress.EnsureSuccessStatusCode();
        Assert.Equal("accepted", (await ReadDataAsync(firstIngress)).GetProperty("decision").GetString());

        using var duplicateIngress = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/ingress", message);
        duplicateIngress.EnsureSuccessStatusCode();
        Assert.Equal("duplicate", (await ReadDataAsync(duplicateIngress)).GetProperty("decision").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync(row => row.WorkspaceTeamId == team);
        Assert.Equal("U_MANAGER_OWNER", enrollment.ClaimedSlackUserId);
        var inboxes = await db.SlackProviderInboxRows.Where(row =>
            row.ProjectId == SlackDeliveryOwnerIds.ManagerProjectId
            && row.ConnectionId == enrollment.Id).ToListAsync();
        Assert.Equal(2, inboxes.Count);
        Assert.All(inboxes, inbox => Assert.NotNull(inbox.DispatchedAt));
        var managerDeliveries = await db.SlackOutboxRows.Where(row =>
            row.OwnerKind == SlackDeliveryOwnerKinds.Manager
            && row.ConnectionId == enrollment.Id).ToListAsync();
        var managerDelivery = Assert.Single(managerDeliveries);
        Assert.DoesNotContain(credentialRef, managerDelivery.PayloadJson, StringComparison.Ordinal);

        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = "connection-foreign-manager-s0",
            ProjectId = "project-foreign-manager-s0",
            AgentId = "agent-foreign-manager-s0",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T_FOREIGN_MANAGER_S0",
            AppId = "A_FOREIGN_MANAGER_S0",
            BotUserId = "U_FOREIGN_MANAGER_S0",
            BotName = "foreign-manager-s0",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            CreatedAt = _fixture.TimeProvider.GetUtcNow(),
            UpdatedAt = _fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var access = scope.ServiceProvider.GetRequiredService<ManagerActorAccessDecider>();
        var authenticated = await access.AuthenticateAsync(team, "U_MANAGER_OWNER");
        Assert.True(authenticated.Allowed);
        var forged = await access.AuthorizeAsync(authenticated.Actor! with { ManagerActorId = "forged" });
        var foreign = await access.AuthorizeAsync(authenticated.Actor!, new ManagerResourceTarget(
            ManagerResourceKinds.Connection,
            "project-foreign-manager-s0",
            "connection-foreign-manager-s0"));
        Assert.False(forged.Allowed);
        Assert.Equal("manager_actor_not_authorized", forged.Reason);
        Assert.False(foreign.Allowed);
        Assert.Equal("manager_resource_not_found", foreign.Reason);

        using var repeatedSetup = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = "A_MANAGER_S0_INGRESS",
            managerBotUserId = "U_MANAGER_S0_INGRESS",
            managerCredentialRef = credentialRef,
        });
        repeatedSetup.EnsureSuccessStatusCode();
        var repeated = await ReadDataAsync(repeatedSetup);
        Assert.False(repeated.TryGetProperty("claimCode", out _));
        Assert.Equal("U_MANAGER_OWNER", repeated.GetProperty("enrollment").GetProperty("claimedSlackUserId").GetString());
    }

    private async Task<SeededAgent> SeedAgentAsync(string status)
    {
        var projectId = $"project_manager_{Guid.NewGuid():N}";
        var agent = new DomainAgent
        {
            Id = $"agent_manager_{Guid.NewGuid():N}",
            ProjectId = projectId,
            Name = "release_helper",
            Description = "Reviews release changes.",
            Status = status,
        };
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Agents.Add(new AgentRow
        {
            Id = agent.Id,
            ProjectId = projectId,
            Name = agent.Name,
            Status = status,
            State = AgentStore.Serialize(agent),
        });
        await db.SaveChangesAsync();
        return new(projectId, agent);
    }

    private static string ManagerPath(string projectId, string suffix = "") =>
        $"/api/projects/{projectId}/slack-manager{suffix}";

    private async Task SetupManagerAsync(string workspaceTeamId)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId,
            managerAppId = $"A_MANAGER_{workspaceTeamId}",
            managerBotUserId = $"U_MANAGER_{workspaceTeamId}",
            managerCredentialRef = $"manager-credential-{workspaceTeamId}",
            transportKind = SlackManagerTransportKind.Socket,
            readiness = SlackManagerReadiness.Ready,
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record SeededAgent(string ProjectId, DomainAgent Agent);
}
