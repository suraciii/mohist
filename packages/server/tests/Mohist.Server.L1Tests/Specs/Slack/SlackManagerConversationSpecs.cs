using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Slack.Services;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Slack;

[Trait("level", "L1")]
public sealed class SlackManagerConversationSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _managerLeases = new(StringComparer.Ordinal);

    public SlackManagerConversationSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Manager_messages_use_one_ordinary_session_and_replays_are_idempotent()
    {
        const string team = "T_MANAGER_CONVERSATION_NEW";
        const string appId = "A_MANAGER_CONVERSATION_NEW";
        const string owner = "U_MANAGER_CONVERSATION_NEW";
        var projectId = $"project_manager_conversation_{Guid.NewGuid():N}";
        await SeedProjectAsync(projectId);
        var enrollmentId = await SetupAndClaimAsync(team, appId, owner);

        var firstText = $"Please inspect {projectId} and report the current state.";
        var first = await SendManagerMessageAsync(appId, team, owner, "1710000000.000003", firstText);
        Assert.Equal("accepted", first.GetProperty("decision").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var sessionId = $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            enrollmentId,
            team,
            "D_MANAGER_CONVERSATION_NEW"))}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var launch = await session.GetInitialLaunchAsync();
        Assert.NotNull(launch);
        Assert.Equal(firstText, launch!.Input!.Text);
        Assert.DoesNotContain("mohistManagerTool", launch.Input.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Manager request accepted", launch.Input.Text, StringComparison.Ordinal);
        Assert.Equal(AgentOriginMarkers.SlackManager, launch.Input.Provenance?.OriginMarker);
        Assert.Equal(AgentOriginMarkers.SlackManager, (await session.ListInputsAsync()).Single().OriginMarker);

        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "runtime-manager-conversation-new",
            "/mohist-tests/manager-conversation-new"));
        var followup = await SendManagerMessageAsync(
            appId,
            team,
            owner,
            "1710000000.000004",
            "Continue with the same conversation and summarize the result.");
        Assert.Equal("accepted", followup.GetProperty("decision").GetString());
        Assert.Equal(sessionId, followup.GetProperty("sessionId").GetString());

        var turnsBeforeReplay = (await session.ListTurnsAsync()).Select(turn => turn.Id).Order().ToArray();
        database.ChangeTracker.Clear();
        var firstInbox = await database.SlackProviderInboxRows.SingleAsync(row =>
            row.ConnectionId == enrollmentId
            && row.SlackMessageIdentity == $"{team}/D_MANAGER_CONVERSATION_NEW/1710000000.000003");
        firstInbox.DispatchedAt = null;
        await database.SaveChangesAsync();

        var replay = await SendManagerMessageAsync(appId, team, owner, "1710000000.000003", firstText);
        Assert.Equal("duplicate", replay.GetProperty("decision").GetString());
        database.ChangeTracker.Clear();
        var turnsAfterReplay = (await session.ListTurnsAsync()).Select(turn => turn.Id).Order().ToArray();
        Assert.Equal(turnsBeforeReplay, turnsAfterReplay);

        var mapping = await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>()
            .GetCurrentSessionIdAsync(
                BuiltInAgentCatalog.MohistSlackProjectId,
                enrollmentId,
                team,
                "D_MANAGER_CONVERSATION_NEW");
        Assert.Equal(sessionId, mapping);
        Assert.Empty(await database.Agents.Where(row => row.ProjectId == projectId).ToListAsync());
        Assert.Null(scope.ServiceProvider.GetService<SlackManagerToolExecutionFenceStore>());
    }

    [Fact]
    public async Task Initial_mapping_is_published_before_submit_and_concurrent_message_is_one_followup()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var team = $"T_MANAGER_MAPPING_FENCE_{suffix}";
        var appId = $"A_MANAGER_MAPPING_FENCE_{suffix}";
        var owner = $"U_MANAGER_MAPPING_FENCE_{suffix}";
        var enrollmentId = await SetupAndClaimAsync(team, appId, owner);
        var firstTs = "1710001000.000010";
        var sessionId = $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            enrollmentId,
            team,
            "D_MANAGER_CONVERSATION_NEW"))}";

        // Scope the fence to THIS session's initial-launch participant: an
        // unscoped one-shot SubmitJob block can be stolen by any unrelated
        // background launch in the shared host, releasing the wait before
        // this conversation published its mapping. EnsureInitialLaunch
        // carries the deterministic session id and fires before submit.
        _fixture.LaunchFaults.BlockNext(
            LaunchParticipantGate.EnsureInitialLaunch,
            participantId => participantId == sessionId);
        try
        {
            var firstTask = SendManagerMessageAsync(
                appId, team, owner, firstTs, "Inspect the current Manager state.");
            await _fixture.LaunchFaults.WaitUntilBlockedAsync(LaunchParticipantGate.EnsureInitialLaunch);

            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var mapping = await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>()
                    .GetCurrentSessionIdAsync(
                        BuiltInAgentCatalog.MohistSlackProjectId,
                        enrollmentId,
                        team,
                        "D_MANAGER_CONVERSATION_NEW");
                Assert.Equal(sessionId, mapping);
            }

            var laterTask = SendManagerMessageAsync(
                appId, team, owner, "1710001000.000011", "Also summarize the result.");
            var later = await laterTask;
            Assert.Equal("accepted", later.GetProperty("decision").GetString());
            Assert.Equal(sessionId, later.GetProperty("sessionId").GetString());

            _fixture.LaunchFaults.ReleaseBlocked(LaunchParticipantGate.EnsureInitialLaunch);
            var first = await firstTask;
            Assert.Equal("accepted", first.GetProperty("decision").GetString());

            var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            Assert.Equal(2, (await session.ListTurnsAsync()).Count);
            await using var finalScope = _fixture.Services.CreateAsyncScope();
            var database = finalScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            Assert.Single(await database.AgentJobs.Where(row => row.AgentSessionId == sessionId).ToListAsync());
        }
        finally
        {
            _fixture.LaunchFaults.ReleaseBlocked(LaunchParticipantGate.EnsureInitialLaunch);
        }
    }

    [Fact]
    public async Task Replay_during_initial_launch_never_creates_a_second_input()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var team = $"T_MANAGER_REPLAY_RACE_{suffix}";
        var appId = $"A_MANAGER_REPLAY_RACE_{suffix}";
        var owner = $"U_MANAGER_REPLAY_RACE_{suffix}";
        var enrollmentId = await SetupAndClaimAsync(team, appId, owner);
        var firstTs = "1710002000.000010";
        var sessionId = $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            enrollmentId,
            team,
            "D_MANAGER_CONVERSATION_NEW"))}";

        _fixture.LaunchFaults.BlockNext(
            LaunchParticipantGate.EnsureInitialLaunch,
            participantId => participantId == sessionId);
        try
        {
            var firstTask = SendManagerMessageAsync(
                appId, team, owner, firstTs, "Inspect the current Manager state.");
            await _fixture.LaunchFaults.WaitUntilBlockedAsync(LaunchParticipantGate.EnsureInitialLaunch);

            // Slack replays the SAME message while the initial launch is
            // still between inbox acceptance and route stamping: the replay
            // must be the existing acceptance, never a second input, turn,
            // or management execution.
            var replay = await SendManagerMessageAsync(
                appId, team, owner, firstTs, "Inspect the current Manager state.");
            Assert.Equal("duplicate", replay.GetProperty("decision").GetString());

            _fixture.LaunchFaults.ReleaseBlocked(LaunchParticipantGate.EnsureInitialLaunch);
            var first = await firstTask;
            Assert.Equal("accepted", first.GetProperty("decision").GetString());

            var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            Assert.Single(await session.ListTurnsAsync());
            await using var scope = _fixture.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            Assert.Single(await database.AgentJobs.Where(row => row.AgentSessionId == sessionId).ToListAsync());
        }
        finally
        {
            _fixture.LaunchFaults.ReleaseBlocked(LaunchParticipantGate.EnsureInitialLaunch);
        }
    }

    [Fact]
    public async Task Missing_runtime_replaces_the_mapping_and_accepts_the_current_message_once()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var team = $"T_MANAGER_RECOVERY_{suffix}";
        var appId = $"A_MANAGER_RECOVERY_{suffix}";
        var owner = $"U_MANAGER_RECOVERY_{suffix}";
        var enrollmentId = await SetupAndClaimAsync(team, appId, owner);

        var initial = await SendManagerMessageAsync(
            appId, team, owner, "1710000001.000002", "Start the Manager conversation.");
        Assert.Equal("accepted", initial.GetProperty("decision").GetString());
        await using (var beforeScope = _fixture.Services.CreateAsyncScope())
        {
            var beforeMapping = await beforeScope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>()
                .GetCurrentSessionIdAsync(
                    BuiltInAgentCatalog.MohistSlackProjectId,
                    enrollmentId,
                    team,
                    "D_MANAGER_CONVERSATION_NEW");
            Assert.NotNull(beforeMapping);
        }
        var originalSessionId = $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            enrollmentId,
            team,
            "D_MANAGER_CONVERSATION_NEW"))}";

        var replacementTs = "1710000001.000003";
        var replacement = await SendManagerMessageAsync(
            appId, team, owner, replacementTs, "Continue after the runtime session disappeared.");
        Assert.Equal("accepted", replacement.GetProperty("decision").GetString());
        var replacementSessionId = $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            enrollmentId,
            team,
            "D_MANAGER_CONVERSATION_NEW",
            replacementTs))}";
        Assert.NotEqual(originalSessionId, replacementSessionId);
        Assert.Equal(replacementSessionId, replacement.GetProperty("sessionId").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var mapping = await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>()
            .GetCurrentSessionIdAsync(
                BuiltInAgentCatalog.MohistSlackProjectId,
                enrollmentId,
                team,
                "D_MANAGER_CONVERSATION_NEW");
        Assert.Equal(replacementSessionId, mapping);
        var database = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var replacementInbox = await database.SlackProviderInboxRows.SingleAsync(row =>
            row.ConnectionId == enrollmentId
            && row.SlackMessageIdentity == $"{team}/D_MANAGER_CONVERSATION_NEW/{replacementTs}");
        Assert.Equal(replacementSessionId, replacementInbox.RouteSessionId);

        var replay = await SendManagerMessageAsync(appId, team, owner, replacementTs, "Continue after the runtime session disappeared.");
        Assert.Equal("duplicate", replay.GetProperty("decision").GetString());
        Assert.Single(await database.AgentJobs.Where(row => row.AgentSessionId == replacementSessionId).ToListAsync());
    }

    private async Task<string> SetupAndClaimAsync(string team, string appId, string owner)
    {
        using var setupResponse = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = appId,
            managerBotUserId = $"U_MANAGER_BOT_{team}",
        });
        setupResponse.EnsureSuccessStatusCode();
        var claimCode = (await ReadDataAsync(setupResponse)).GetProperty("claimCode").GetString()!;
        var enrollmentId = await SlackRuntimeLeaseTestSupport.ProvisionVerifiedManagerAsync(
            _fixture, team, $"xapp-manager-{team}", $"xoxb-manager-{team}");
        _managerLeases[team] = await SlackRuntimeLeaseTestSupport.AcquireManagerLeaseAsync(
            _fixture, enrollmentId, team);
        var claimed = await SendManagerMessageAsync(appId, team, owner, "1710000000.000001", $"claim {claimCode}");
        Assert.Equal("accepted", claimed.GetProperty("decision").GetString());
        return enrollmentId;
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
            conversationId = "D_MANAGER_CONVERSATION_NEW",
            messageTs,
            senderSlackUserId = sender,
            text,
            isDirectMessage = true,
            leaseId = _managerLeases[team],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Manager ingress returned {(int)response.StatusCode}: {raw}");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
