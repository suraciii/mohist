using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackDmNewTaskIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackDmNewTaskIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task New_task_creates_work_and_switches_the_current_session()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostIngressAsync(connection, "D-DM-NEW", "1710000000.000100", "first task");
        var firstSessionId = first.GetProperty("sessionId").GetString();
        var firstJobKey = first.GetProperty("jobKey").GetString();

        var second = await PostIngressAsync(connection, "D-DM-NEW", "1710000000.000200", "new task second task");
        var secondSessionId = second.GetProperty("sessionId").GetString();
        var secondJobKey = second.GetProperty("jobKey").GetString();

        Assert.True(second.GetProperty("newTask").GetBoolean());
        Assert.NotEqual(firstSessionId, secondSessionId);
        Assert.NotEqual(firstJobKey, secondJobKey);
        Assert.Contains("Starting a new task", await ReadReplyAsync(connection, "D-DM-NEW", "1710000000.000200"));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Equal(secondSessionId, await mapping.GetCurrentSessionIdAsync(
            connection.ProjectId, connection.Id, "D-DM-NEW"));
        Assert.Equal(2, await db.AgentSessions.CountAsync(row => row.LabelConnectionId == connection.Id
            && row.LabelSlackConversationId == "D-DM-NEW"));
        Assert.Equal(2, await db.AgentJobs.CountAsync(row => row.ProjectId == connection.ProjectId));
    }

    [Fact]
    public async Task New_task_does_not_cancel_prior_running_work()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = $"slack-new-task-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(connection.ProjectId, runnerId);

        var first = await PostIngressAsync(connection, "D-DM-RUNNING", "1710000000.000300", "long running task");
        var firstJobKey = first.GetProperty("jobKey").GetString()!;
        var firstDispatch = await AcceptLaunchAsync(firstJobKey, runnerId);
        var firstJob = _fixture.Grains.GetGrain<IAgentJobGrain>(firstJobKey);
        Assert.Equal(AgentJobStatus.Running, await firstJob.GetStatusAsync());

        var second = await PostIngressAsync(connection, "D-DM-RUNNING", "1710000000.000400", "new task independent task");
        Assert.True(second.GetProperty("newTask").GetBoolean());
        Assert.Equal(AgentJobStatus.Running, await firstJob.GetStatusAsync());

        var report = await firstJob.ReportResultAsync(
            firstDispatch.RunnerId,
            firstDispatch.WorkId,
            new WorkResult("completed", "prior work completed"));

        Assert.True(report.Accepted);
        Assert.Equal(AgentJobStatus.Completed, (await firstJob.GetTerminalResultAsync()).Status);
    }

    [Fact]
    public async Task A_normal_message_containing_new_task_words_remains_a_followup()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostIngressAsync(connection, "D-DM-MARKER", "1710000000.000500", "first task");
        var firstSessionId = first.GetProperty("sessionId").GetString();

        var followup = await PostIngressAsync(
            connection,
            "D-DM-MARKER",
            "1710000000.000600",
            "please review the new task wording");

        Assert.True(followup.GetProperty("followup").GetBoolean());
        Assert.Equal(firstSessionId, followup.GetProperty("sessionId").GetString());
        Assert.False(followup.TryGetProperty("newTask", out _));
    }

    [Fact]
    public async Task Empty_new_task_is_rejected_without_accepting_or_creating_work()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostIngressAsync(connection, "D-DM-EMPTY", "1710000000.000700", "first task");
        var firstSessionId = first.GetProperty("sessionId").GetString();

        var rejected = await PostIngressAsync(connection, "D-DM-EMPTY", "1710000000.000800", "NEW TASK   ");

        Assert.Equal("rejected", rejected.GetProperty("kind").GetString());
        Assert.Equal("Please send a task for the Agent to perform.", rejected.GetProperty("reason").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Equal(firstSessionId, await db.SlackDmSessionMappings
            .Where(row => row.ConnectionId == connection.Id && row.DmConversationId == "D-DM-EMPTY")
            .Select(row => row.CurrentSessionId)
            .SingleAsync());
        Assert.DoesNotContain(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id && row.DmConversationId == "D-DM-EMPTY")
            .Select(row => row.SlackMessageIdentity)
            .ToListAsync(), identity => identity.EndsWith("1710000000.000800", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Redelivered_new_task_does_not_restore_an_older_current_session()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostIngressAsync(connection, "D-DM-REPLAY-NEW", "1710000000.000100", "new task first work");
        var second = await PostIngressAsync(connection, "D-DM-REPLAY-NEW", "1710000000.000200", "new task second work");

        var replay = await PostIngressAsync(connection, "D-DM-REPLAY-NEW", "1710000000.000100", "new task first work");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
        Assert.Equal(second.GetProperty("sessionId").GetString(), await mapping.GetCurrentSessionIdAsync(
            connection.ProjectId, connection.Id, "D-DM-REPLAY-NEW"));
        Assert.Equal(first.GetProperty("sessionId").GetString(), replay.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Concurrent_new_tasks_keep_the_latest_message_as_current()
    {
        var connection = await CreateConnectionAsync();
        var results = await Task.WhenAll(
            PostIngressAsync(connection, "D-DM-CONCURRENT", "1710000000.000100", "new task first work"),
            PostIngressAsync(connection, "D-DM-CONCURRENT", "1710000000.000200", "new task second work"));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
        Assert.Equal(results[1].GetProperty("sessionId").GetString(), await mapping.GetCurrentSessionIdAsync(
            connection.ProjectId, connection.Id, "D-DM-CONCURRENT"));
    }

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
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
        using var slots = await _fixture.Client.PatchAsJsonAsync($"/api/runner/{runnerId}", new { slots = 1 });
        slots.EnsureSuccessStatusCode();
    }

    private async Task<(string RunnerId, string WorkId)> AcceptLaunchAsync(string jobKey, string runnerId)
    {
        var assignment = await _fixture.AgentJobDispatches.WaitForRunnerAcceptedAsync(jobKey);
        Assert.Equal(runnerId, assignment.RunnerId);
        using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", null);
        var dispatch = Assert.Single(await poll.ReadDispatchElementsAsync());
        return (runnerId, dispatch.GetProperty("workId").GetString()!);
    }

    private async Task<string> ReadReplyAsync(AgentConnection connection, string conversationId, string messageTs)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var payload = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.DmConversationId == conversationId
                && row.Kind == SlackOutboxKinds.UserAction
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
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
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
