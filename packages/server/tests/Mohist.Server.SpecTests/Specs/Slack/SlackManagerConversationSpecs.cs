using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagerConversationSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _managerLeases = new(StringComparer.Ordinal);

    public SlackManagerConversationSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Claimed_manager_conversation_uses_a_session_and_server_authorizes_every_model_tool_request()
    {
        const string team = "T_MANAGER_CONVERSATION";
        const string appId = "A_MANAGER_CONVERSATION";
        const string owner = "U_MANAGER_CONVERSATION";
        var projectId = $"project_manager_conversation_{Guid.NewGuid():N}";
        await SeedProjectAsync(projectId);

        using var setupResponse = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = appId,
            managerBotUserId = "U_MANAGER_BOT_CONVERSATION",
        });
        setupResponse.EnsureSuccessStatusCode();
        var claimCode = (await ReadDataAsync(setupResponse)).GetProperty("claimCode").GetString()!;
        var enrollmentId = await SlackRuntimeLeaseTestSupport.ProvisionVerifiedManagerAsync(
            _fixture, team, "xapp-manager-conversation", "xoxb-manager-conversation");
        _managerLeases[team] = await SlackRuntimeLeaseTestSupport.AcquireManagerLeaseAsync(
            _fixture, enrollmentId, team);

        var unclaimed = await SendManagerMessageAsync(
            appId, team, owner, "1710000000.000001", "list");
        Assert.Equal("rejected", unclaimed.GetProperty("decision").GetString());
        Assert.False(unclaimed.GetProperty("deliveryIntentCreated").GetBoolean());

        var claimed = await SendManagerMessageAsync(
            appId, team, owner, "1710000000.000002", $"claim {claimCode}");
        Assert.Equal("accepted", claimed.GetProperty("decision").GetString());

        const string naturalLanguageCreate = "Please create release-helper in PROJECT_PLACEHOLDER to review release changes every day.";
        var firstPrompt = naturalLanguageCreate.Replace("PROJECT_PLACEHOLDER", projectId, StringComparison.Ordinal);
        var acceptedNaturalLanguage = await SendManagerMessageAsync(
            appId,
            team,
            owner,
            "1710000000.000003",
            firstPrompt);
        Assert.Equal("accepted", acceptedNaturalLanguage.GetProperty("decision").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var enrollment = await scope.ServiceProvider.GetRequiredService<SlackWorkspaceEnrollmentStore>()
            .GetActiveByTeamAsync(team);
        Assert.NotNull(enrollment);
        var sessionId = $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            enrollment!.Id,
            team,
            "D_MANAGER_CONVERSATION"))}";
        var session = _fixture.Grains.GetGrain<Mohist.Server.Sessions.Grains.IAgentSessionGrain>(sessionId);
        var launch = await session.GetInitialLaunchAsync();
        Assert.NotNull(launch);
        Assert.Contains("Manager request", launch!.Input!.Text, StringComparison.Ordinal);
        Assert.Contains(firstPrompt, launch.Input.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(claimCode, launch.Input.Text, StringComparison.Ordinal);
        await session.AttachPhysicalSessionAsync(new Mohist.Server.Sessions.Grains.AttachPhysicalSessionCommand(
                "runtime-manager-conversation",
                "/mohist-tests/manager-conversation"));
        var followup = await SendManagerMessageAsync(
            appId,
            team,
            owner,
            "1710000000.000004",
            "Please keep the same release-helper conversation and ask for confirmation.");
        Assert.Equal("accepted", followup.GetProperty("decision").GetString());
        var turnIdsBeforeRedrive = (await session.ListTurnsAsync())
            .Select(turn => turn.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var firstInbox = await database.SlackProviderInboxRows.SingleAsync(row =>
            row.ConnectionId == enrollment.Id
            && row.SlackMessageIdentity == $"{team}/D_MANAGER_CONVERSATION/1710000000.000003");
        firstInbox.DispatchedAt = null;
        await database.SaveChangesAsync();
        var redriven = await SendManagerMessageAsync(
            appId,
            team,
            owner,
            "1710000000.000003",
            firstPrompt);
        Assert.Equal("accepted", redriven.GetProperty("decision").GetString());
        database.ChangeTracker.Clear();
        var redrivenInbox = await database.SlackProviderInboxRows.SingleAsync(row => row.Id == firstInbox.Id);
        Assert.NotNull(redrivenInbox.DispatchedAt);
        var turnIdsAfterRedrive = (await session.ListTurnsAsync())
            .Select(turn => turn.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(turnIdsBeforeRedrive, turnIdsAfterRedrive);
        var currentSession = await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>()
            .GetCurrentSessionIdAsync(
                BuiltInAgentCatalog.MohistSlackProjectId,
                enrollment.Id,
                "D_MANAGER_CONVERSATION");
        Assert.Equal(sessionId, currentSession);
        Assert.Empty(await database.Agents.Where(row => row.ProjectId == projectId).ToListAsync());

        await HandleManagerToolTurnAsync(
            sessionId,
            team,
            owner,
            enrollment.Id,
            "manager-tool-create",
            $"{{\"mohistManagerTool\":{{\"name\":\"create\",\"arguments\":{{\"projectId\":\"{projectId}\",\"agentName\":\"release-helper\",\"dailyResponsibility\":\"review release changes\"}}}}}}");

        database.ChangeTracker.Clear();
        var agent = AgentStore.Deserialize(await database.Agents
            .Where(row => row.ProjectId == projectId && row.Name == "release-helper")
            .Select(row => row.State)
            .SingleAsync());
        Assert.NotNull(agent);
        Assert.Equal("opencode", agent!.AgentConfig!.Value.GetProperty("runtime").GetString());
        var connection = Assert.Single(await database.AgentConnections
            .Where(row => row.ProjectId == projectId && row.AgentId == agent.Id && row.WorkspaceTeamId == team)
            .ToListAsync());
        Assert.Null(connection.DeletedAt);

        await HandleManagerToolTurnAsync(
            sessionId,
            team,
            owner,
            enrollment.Id,
            "manager-tool-create",
            $"{{\"mohistManagerTool\":{{\"name\":\"create\",\"arguments\":{{\"projectId\":\"{projectId}\",\"agentName\":\"release-helper\",\"dailyResponsibility\":\"review release changes\"}}}}}}");

        connection.SetupProgress = SetupProgressKind.Complete;
        await database.SaveChangesAsync();

        await HandleManagerToolTurnAsync(
            sessionId,
            team,
            owner,
            enrollment.Id,
            "manager-tool-transfer",
            $"{{\"mohistManagerTool\":{{\"name\":\"transfer-owner\",\"arguments\":{{\"projectId\":\"{projectId}\",\"connectionId\":\"{connection.Id}\"}}}}}}");
        await HandleManagerToolTurnAsync(
            sessionId,
            team,
            owner,
            enrollment.Id,
            "manager-tool-transfer",
            $"{{\"mohistManagerTool\":{{\"name\":\"transfer-owner\",\"arguments\":{{\"projectId\":\"{projectId}\",\"connectionId\":\"{connection.Id}\"}}}}}}");

        database.ChangeTracker.Clear();
        var guidancePayload = Assert.Single(await database.SlackOutboxRows
            .Where(row => row.OwnerKind == SlackDeliveryOwnerKinds.Manager
                && row.DispatchRef == "manager-tool:manager-tool-transfer:user-instruction")
            .Select(row => row.PayloadJson)
            .ToListAsync());
        var guidance = JsonDocument.Parse(guidancePayload).RootElement.GetProperty("text").GetString()!;
        var toolClaimCode = Regex.Match(guidance, "claim ([A-Z2-9]{10})").Groups[1].Value;
        Assert.NotEmpty(toolClaimCode);
        var sessionState = await database.AgentSessions
            .Where(row => row.Id == sessionId)
            .Select(row => row.State)
            .SingleAsync();
        Assert.DoesNotContain(toolClaimCode, sessionState, StringComparison.Ordinal);
        Assert.Contains("separate user instruction", sessionState, StringComparison.Ordinal);
        Assert.Equal(
            SlackManagerToolExecutionFenceStates.Completed,
            await database.SlackManagerToolExecutionFences
                .Where(row => row.JobKey == "manager-tool-transfer")
                .Select(row => row.State)
                .SingleAsync());

        await HandleManagerToolTurnAsync(
            sessionId,
            team,
            owner,
            enrollment.Id,
            "manager-tool-delete",
            "{\"mohistManagerTool\":{\"name\":\"permanent-delete\",\"arguments\":{}}}");
        await HandleManagerToolTurnAsync(
            sessionId,
            team,
            "U_UNAUTHORIZED",
            enrollment.Id,
            "manager-tool-disable",
            $"{{\"mohistManagerTool\":{{\"name\":\"disable\",\"arguments\":{{\"projectId\":\"{projectId}\",\"connectionId\":\"{connection.Id}\"}}}}}}");

        database.ChangeTracker.Clear();
        var unchanged = await database.AgentConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Equal("enabled", unchanged.DesiredState);
        var messages = await database.SlackOutboxRows
            .Where(row => row.OwnerKind == SlackDeliveryOwnerKinds.Manager)
            .Select(row => row.PayloadJson)
            .ToListAsync();
        Assert.DoesNotContain(messages, payload => payload.Contains("Agent created and mounted.", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, payload => payload.Contains("manager_tool_not_available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Manager_conversation_replaces_an_unbound_session_before_accepting_the_next_message()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var team = $"T_MANAGER_RECOVERY_{suffix}";
        var appId = $"A_MANAGER_RECOVERY_{suffix}";
        var owner = $"U_MANAGER_RECOVERY_{suffix}";
        using var setupResponse = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = appId,
            managerBotUserId = $"U_MANAGER_BOT_RECOVERY_{suffix}",
        });
        setupResponse.EnsureSuccessStatusCode();
        var claimCode = (await ReadDataAsync(setupResponse)).GetProperty("claimCode").GetString()!;
        var enrollmentId = await SlackRuntimeLeaseTestSupport.ProvisionVerifiedManagerAsync(
            _fixture, team, "xapp-manager-recovery", "xoxb-manager-recovery");
        _managerLeases[team] = await SlackRuntimeLeaseTestSupport.AcquireManagerLeaseAsync(
            _fixture, enrollmentId, team);
        var claimed = await SendManagerMessageAsync(
            appId, team, owner, "1710000001.000001", $"claim {claimCode}");
        Assert.Equal("accepted", claimed.GetProperty("decision").GetString());

        var initial = await SendManagerMessageAsync(
            appId, team, owner, "1710000001.000002", "Start a Manager conversation.");
        Assert.Equal("accepted", initial.GetProperty("decision").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var enrollment = await scope.ServiceProvider.GetRequiredService<SlackWorkspaceEnrollmentStore>()
            .GetActiveByTeamAsync(team);
        Assert.NotNull(enrollment);
        var originalSessionId = $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            enrollment!.Id,
            team,
            "D_MANAGER_CONVERSATION"))}";
        var replacementMessageTs = "1710000001.000003";
        var replacement = await SendManagerMessageAsync(
            appId, team, owner, replacementMessageTs, "Continue after the unbound launch.");
        Assert.Equal("accepted", replacement.GetProperty("decision").GetString());

        var replacementSessionId = $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            enrollment.Id,
            team,
            "D_MANAGER_CONVERSATION",
            replacementMessageTs))}";
        Assert.NotEqual(originalSessionId, replacementSessionId);
        var mapping = await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>()
            .GetCurrentSessionIdAsync(
                BuiltInAgentCatalog.MohistSlackProjectId,
                enrollment.Id,
                "D_MANAGER_CONVERSATION");
        Assert.Equal(replacementSessionId, mapping);

        var database = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var replacementJob = await database.AgentJobs.SingleAsync(row =>
            row.AgentSessionId == replacementSessionId);
        Assert.Equal(BuiltInAgentCatalog.MohistSlackProjectId, replacementJob.ProjectId);
        var replacementInbox = await database.SlackProviderInboxRows.SingleAsync(row =>
            row.ConnectionId == enrollment.Id
            && row.SlackMessageIdentity == $"{team}/D_MANAGER_CONVERSATION/{replacementMessageTs}");
        Assert.Equal(replacementSessionId, replacementInbox.RouteSessionId);
    }

    private async Task SeedProjectAsync(string projectId)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        database.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await database.SaveChangesAsync();
    }

    private async Task<JsonElement> SendManagerMessageAsync(
        string appId,
        string team,
        string sender,
        string messageTs,
        string text)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/ingress", new
        {
            appId,
            workspaceTeamId = team,
            conversationId = "D_MANAGER_CONVERSATION",
            messageTs,
            senderSlackUserId = sender,
            text,
            isDirectMessage = true,
            leaseId = _managerLeases[team],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        response.EnsureSuccessStatusCode();
        return await ReadDataAsync(response);
    }

    private async Task HandleManagerToolTurnAsync(
        string sessionId,
        string team,
        string slackUserId,
        string enrollmentId,
        string jobKey,
        string assistantText)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var handler = new SlackTerminalDeliveryHandler(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlackTerminalDeliveryHandler>.Instance);
        var delivery = new
        {
            jobKey,
            workLabel = "Manager request",
            connectionId = enrollmentId,
            workspaceTeamId = team,
            slackUserId,
            conversationId = "D_MANAGER_CONVERSATION",
            status = "completed",
            message = "AgentJob completed",
            failureReason = (string?)null,
            failureCategory = (string?)null,
            artifactCount = 0,
            exitCode = 0,
            assistantText,
        };
        var evt = new CloudEvent(
            $"delivery:{jobKey}",
            new Uri($"/mohist/agent-session/{sessionId}", UriKind.Relative),
            EventCatalog.ReverseDns.AgentSessionFollowupDelivery,
            _fixture.TimeProvider.GetUtcNow(),
            JsonSerializer.SerializeToElement(delivery),
            subject: sessionId,
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = BuiltInAgentCatalog.MohistSlackProjectId,
            });
        await handler.HandleAsync(evt, CancellationToken.None);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
