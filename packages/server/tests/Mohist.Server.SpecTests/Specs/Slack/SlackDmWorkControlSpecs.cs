using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackDmWorkControlSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly List<string> _runnerIds = [];

    public SlackDmWorkControlSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var runnerId in _runnerIds)
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
    }

    [Fact]
    public async Task Cancel_cancels_the_current_queued_launch_turn()
    {
        var connection = await CreateConnectionAsync();
        var launch = await PostIngressAsync(connection, "D-DM-CANCEL", "1710000000.000100", "queued work");

        var cancel = await PostIngressAsync(connection, "D-DM-CANCEL", "1710000000.000200", "CANCEL");

        Assert.Equal("cancelled", cancel.GetProperty("kind").GetString());
        Assert.Contains("Work cancelled", await ReadReplyAsync(connection, "D-DM-CANCEL", "1710000000.000200"));
        Assert.Equal(AgentJobStatus.Cancelled,
            await _fixture.Grains.GetGrain<IAgentJobGrain>(launch.GetProperty("jobKey").GetString()!).GetStatusAsync());
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(launch.GetProperty("sessionId").GetString()!);
        Assert.Equal(AgentTurnStatus.Cancelled, Assert.Single(await session.ListTurnsAsync()).Status);
    }

    [Fact]
    public async Task Stop_requests_a_runtime_stop_for_the_current_executing_turn()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = $"slack-stop-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(connection.ProjectId, runnerId);
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(runnerId, $"{runnerId}-connection");
        await SeedExecutingSessionAsync(connection, "D-DM-STOP", runnerId, setCurrent: true);

        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        var stop = await PostIngressAsync(connection, "D-DM-STOP", "1710000000.000400", "stop");

        Assert.Equal("stopped", stop.GetProperty("kind").GetString());
        Assert.Contains("Work stopped", await ReadReplyAsync(connection, "D-DM-STOP", "1710000000.000400"));
        var invocation = Assert.Single(hub.Invocations);
        Assert.Equal("CancelAgentSession", invocation.Method);
        var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
        Assert.Equal(stop.GetProperty("turnId").GetString(), payload.GetProperty("turnId").GetString());
    }

    [Fact]
    public async Task Cancel_of_an_executing_turn_explains_that_stop_is_required()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = $"slack-cancel-running-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(connection.ProjectId, runnerId);
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(runnerId, $"{runnerId}-connection");
        await SeedExecutingSessionAsync(connection, "D-DM-CANCEL-RUNNING", runnerId, setCurrent: true);

        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();
        var cancel = await PostIngressAsync(connection, "D-DM-CANCEL-RUNNING", "1710000000.000600", "cancel");

        Assert.Equal("executing", cancel.GetProperty("kind").GetString());
        Assert.Contains("use stop", await ReadReplyAsync(connection, "D-DM-CANCEL-RUNNING", "1710000000.000600"));
        Assert.Empty(hub.Invocations);
    }

    [Fact]
    public async Task Terminal_current_work_does_not_stop_a_later_session()
    {
        var connection = await CreateConnectionAsync();
        var current = await SeedQueuedSessionAsync(connection, "D-DM-STALE");
        await PostIngressAsync(connection, "D-DM-STALE", "1710000000.000700", "cancel");
        var later = await SeedExecutingSessionAsync(connection, "D-DM-STALE");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();

        var stop = await PostIngressAsync(connection, "D-DM-STALE", "1710000000.000800", "stop");

        Assert.Equal("already_ended", stop.GetProperty("kind").GetString());
        Assert.Contains("already ended", await ReadReplyAsync(connection, "D-DM-STALE", "1710000000.000800"));
        Assert.Empty(hub.Invocations);
        Assert.Equal(AgentTurnStatus.Cancelled,
            Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(current.SessionId).ListTurnsAsync()).Status);
        Assert.Equal(AgentTurnStatus.Executing,
            Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(later.SessionId).ListTurnsAsync()).Status);
    }

    [Fact]
    public async Task Control_command_without_current_work_reports_no_active_work()
    {
        var connection = await CreateConnectionAsync();

        var stop = await PostIngressAsync(connection, "D-DM-NONE", "1710000000.000900", "stop");

        Assert.Equal("no_active_work", stop.GetProperty("kind").GetString());
        Assert.Contains("no active work", await ReadReplyAsync(connection, "D-DM-NONE", "1710000000.000900"));
    }

    [Fact]
    public async Task Cancel_only_changes_the_most_recent_queued_turn()
    {
        var connection = await CreateConnectionAsync();
        var turns = await SeedTwoQueuedSessionAsync(connection, "D-DM-SINGLE");

        var cancel = await PostIngressAsync(connection, "D-DM-SINGLE", "1710000000.001000", "cancel");

        Assert.Equal("cancelled", cancel.GetProperty("kind").GetString());
        var records = await _fixture.Grains.GetGrain<IAgentSessionGrain>(turns.SessionId).ListTurnsAsync();
        Assert.Equal(AgentTurnStatus.Completed, records.Single(turn => turn.Id == turns.FirstTurnId).Status);
        Assert.Equal(AgentTurnStatus.Cancelled, records.Single(turn => turn.Id == turns.SecondTurnId).Status);
    }

    [Fact]
    public async Task Redelivered_cancel_does_not_affect_later_current_work()
    {
        var connection = await CreateConnectionAsync();
        var cancelled = await SeedQueuedSessionAsync(connection, "D-DM-CONTROL-REPLAY");
        await PostIngressAsync(connection, "D-DM-CONTROL-REPLAY", "1710000000.001100", "cancel");
        var later = await SeedExecutingSessionAsync(connection, "D-DM-CONTROL-REPLAY", setCurrent: true);
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();

        var replay = await PostIngressAsync(connection, "D-DM-CONTROL-REPLAY", "1710000000.001100", "cancel");

        Assert.Equal("already_ended", replay.GetProperty("kind").GetString());
        Assert.Equal(AgentTurnStatus.Cancelled,
            Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(cancelled.SessionId).ListTurnsAsync()).Status);
        Assert.Equal(AgentTurnStatus.Executing,
            Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(later.SessionId).ListTurnsAsync()).Status);
        Assert.Empty(hub.Invocations);
    }

    private async Task<(string SessionId, string TurnId)> SeedQueuedSessionAsync(
        AgentConnection connection,
        string conversationId)
    {
        var sessionId = $"slack-queued-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/mohist-tests/slack-control",
            Metadata: ConnectionMetadata(connection, conversationId)));
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", turnId, "queued work", "user"));
        await using var scope = _fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>().SetCurrentSessionIdAsync(
            connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER", conversationId, sessionId);
        return (sessionId, turnId);
    }

    private async Task<(string SessionId, string FirstTurnId, string SecondTurnId)> SeedTwoQueuedSessionAsync(
        AgentConnection connection,
        string conversationId)
    {
        var sessionId = $"slack-two-queued-{Guid.NewGuid():N}";
        var firstTurnId = $"turn-{Guid.NewGuid():N}";
        var secondTurnId = $"turn-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/mohist-tests/slack-control",
            Metadata: ConnectionMetadata(connection, conversationId)));
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", firstTurnId, "first queued work", "user"));
        await session.MarkTurnTerminalAsync(firstTurnId, AgentTurnStatus.Completed, null);
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", secondTurnId, "second queued work", "user"));
        await using var scope = _fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>().SetCurrentSessionIdAsync(
            connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER", conversationId, sessionId);
        return (sessionId, firstTurnId, secondTurnId);
    }

    private async Task<(string SessionId, string TurnId)> SeedExecutingSessionAsync(
        AgentConnection connection,
        string conversationId,
        string runnerId = "later-runner",
        bool setCurrent = false)
    {
        var sessionId = $"slack-later-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: "opencode",
            WorkDir: "/mohist-tests/slack-control",
            Metadata: ConnectionMetadata(connection, conversationId)));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "later-runtime", "/mohist-tests/slack-control"));
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", turnId, "later work", "user"));
        await session.MarkTurnExecutingAsync(turnId);
        if (setCurrent)
        {
            await using var scope = _fixture.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>().SetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER", conversationId, sessionId);
        }
        return (sessionId, turnId);
    }

    private static AgentSessionMetadata ConnectionMetadata(AgentConnection connection, string conversationId) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = connection.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [AgentSessionQueryMetadataKeys.ConnectionId] = connection.Id,
            [AgentSessionQueryMetadataKeys.SlackUserId] = "U_OWNER",
            [AgentSessionQueryMetadataKeys.SlackConversationId] = conversationId,
            [GenericAgentSessionMetadata.AgentId] = "agent-control",
            [GenericAgentSessionMetadata.AgentName] = "Mohist Agent",
        });

    private async Task<JsonElement> PostIngressAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string text)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            senderSlackUserId = "U_OWNER",
            text,
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task RegisterRunnerAsync(string projectId, string runnerId)
    {
        using var register = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId,
        });
        register.EnsureSuccessStatusCode();
        _runnerIds.Add(runnerId);
        using var slots = await _fixture.Client.PatchAsJsonAsync($"/api/runner/{runnerId}", new { slots = 1 });
        slots.EnsureSuccessStatusCode();
    }

    private async Task<string> ReadReplyAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var payload = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.DmConversationId == conversationId
                && row.DispatchRef == $"slack-ack:T123/{conversationId}/{messageTs}")
            .Select(row => row.PayloadJson)
            .SingleAsync();
        return JsonDocument.Parse(payload).RootElement.GetProperty("text").GetString()!;
    }

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        _fixture.Slack.UsersInfo = new(true, null, new("U_OWNER", "T123", false, false, false, false, false));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow { Id = projectId, Name = projectId, CreatedAt = now, UpdatedAt = now });
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = "Mohist Agent",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = "Mohist Agent",
                Status = AgentStatus.Active,
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T123",
            AppId = "A123",
            BotUserId = "U123",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_OWNER",
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-old"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-old"));
        return new AgentConnection { Id = id, ProjectId = projectId, WorkspaceTeamId = "T123" };
    }

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";
}
