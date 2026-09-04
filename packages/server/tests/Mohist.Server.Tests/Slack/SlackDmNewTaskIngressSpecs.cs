using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Collection("SlackApiSurface")]
[Trait("level", "L1")]
public sealed class SlackDmNewTaskIngressSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);
    private readonly List<string> _runnerIds = [];

    public SlackDmNewTaskIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var runnerId in _runnerIds)
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
    }

    [Fact]
    public async Task New_task_creates_work_and_switches_the_current_session()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = $"slack-new-task-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(connection.ProjectId, runnerId);

        var first = await PostIngressAsync(connection, "D-DM-NEW", "1710000000.000100", "first task");
        var firstSessionId = first.GetProperty("sessionId").GetString();
        var firstJobKey = first.GetProperty("jobKey").GetString()!;
        var firstDispatch = await AcceptLaunchAsync(firstJobKey, runnerId, connection.ProjectId);
        var firstJob = _fixture.Grains.GetGrain<IAgentJobGrain>(firstJobKey);
        Assert.Equal(AgentJobStatus.Running, await firstJob.GetStatusAsync());

        var second = await PostIngressAsync(connection, "D-DM-NEW", "1710000000.000200", "new task second task");
        var secondSessionId = second.GetProperty("sessionId").GetString();
        var secondJobKey = second.GetProperty("jobKey").GetString();

        Assert.True(second.GetProperty("newTask").GetBoolean());
        Assert.Equal(AgentJobStatus.Running, await firstJob.GetStatusAsync());
        Assert.NotEqual(firstSessionId, secondSessionId);
        Assert.NotEqual(firstJobKey, secondJobKey);
        await AssertReceivedProjectionAsync(connection, "D-DM-NEW", "1710000000.000200");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var firstProgress = await db.SlackOutboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.ConversationId == "D-DM-NEW"
            && row.ThreadTs == "1710000000.000100"
            && row.Kind == SlackOutboxKinds.ReplaceableProgress
            && row.DispatchRef == SlackStatusProjection.DispatchRef(
                new SlackMessageIdentity("T123", "D-DM-NEW", "1710000000.000100"), "progress"));
        var secondProgress = await db.SlackOutboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.ConversationId == "D-DM-NEW"
            && row.ThreadTs == "1710000000.000200"
            && row.Kind == SlackOutboxKinds.ReplaceableProgress
            && row.DispatchRef == SlackStatusProjection.DispatchRef(
                new SlackMessageIdentity("T123", "D-DM-NEW", "1710000000.000200"), "progress"));
        var firstPayload = SlackDeliveryPayload.Parse(firstProgress.PayloadJson);
        var secondPayload = SlackDeliveryPayload.Parse(secondProgress.PayloadJson);
        Assert.Contains($"Session: {firstSessionId}", firstPayload.Text, StringComparison.Ordinal);
        Assert.Contains($"Session: {secondSessionId}", secondPayload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Working", firstPayload.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Working", secondPayload.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SlackTurnControlService.StopActionId, firstPayload.Blocks?.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(SlackTurnControlService.StopActionId, secondPayload.Blocks?.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-", firstProgress.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-", secondProgress.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(secondSessionId, await mapping.GetCurrentSessionIdAsync(
            connection.ProjectId, connection.Id, "D-DM-NEW"));
        Assert.Equal(2, await db.AgentSessions.CountAsync(row => row.LabelConnectionId == connection.Id
            && row.LabelSlackConversationId == "D-DM-NEW"));
        Assert.Equal(2, await db.AgentJobs.CountAsync(row => row.ProjectId == connection.ProjectId));

        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var sessionId = firstDispatch.Dispatch.AgentSessionId!;
        var turnId = firstDispatch.Dispatch.InitialTurnId!;
        var runtime = firstDispatch.Dispatch.AgentDefinition!.Runtime;
        Assert.True(await firstJob.RecordRuntimeSessionBindingAsync(
            firstDispatch.RunnerId, firstDispatch.WorkId, sessionId, runtimeSessionId));
        var report = await firstJob.ReportResultAsync(
            firstDispatch.RunnerId,
            firstDispatch.WorkId,
            new WorkResult(
                "completed",
                "prior work completed",
                AgentSessionId: sessionId,
                AgentTurnId: turnId,
                Runtime: runtime,
                RuntimeSessionId: runtimeSessionId));

        Assert.True(report.Accepted);
        Assert.Equal(AgentJobStatus.Completed, (await firstJob.GetTerminalResultAsync()).Status);
    }

    [Fact]
    public async Task Established_ordinary_dm_followup_bypasses_the_new_work_gate()
    {
        var connection = await CreateConnectionAsync();
        var initial = await PostIngressAsync(connection, "D-DM-FOLLOWUP-READY", "1710000000.001300", "initial task");
        var sessionId = initial.GetProperty("sessionId").GetString();
        await SetAgentConfigAsync(connection, null);

        var followup = await PostIngressAsync(
            connection,
            "D-DM-FOLLOWUP-READY",
            "1710000000.001400",
            "ordinary follow-up");

        Assert.True(followup.GetProperty("followup").GetBoolean());
        Assert.Equal(sessionId, followup.GetProperty("sessionId").GetString());
        Assert.Empty(await GetAdmissionNudgesAsync(connection, "D-DM-FOLLOWUP-READY"));
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
            .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "D-DM-EMPTY")
            .Select(row => row.SlackMessageIdentity)
            .ToListAsync(), identity => identity.EndsWith("1710000000.000800", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Thread_origin_is_retained_on_the_session_metadata_and_delivery_ack()
    {
        var connection = await CreateConnectionAsync();
        var result = await PostIngressAsync(
            connection,
            "C-THREAD",
            "1710000000.000450",
            "thread task",
            "1710000000.000400");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var session = await db.AgentSessions
            .SingleAsync(row => row.Id == result.GetProperty("sessionId").GetString());
        Assert.Equal("C-THREAD", session.LabelSlackConversationId);
        Assert.Equal("1710000000.000400", session.LabelSlackThreadTs);

        var received = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-THREAD"
                && row.ThreadTs == "1710000000.000400"
                && row.DispatchRef == SlackStatusProjection.DispatchRef(
                    new SlackMessageIdentity("T123", "C-THREAD", "1710000000.000450"), "received"))
            .Select(row => row.PayloadJson)
            .SingleAsync();
        Assert.Equal(SlackDeliveryOperations.ReactionAdd, SlackDeliveryPayload.Parse(received).Operation);
        var progress = await db.SlackOutboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.ConversationId == "C-THREAD"
            && row.ThreadTs == "1710000000.000400"
            && row.Kind == SlackOutboxKinds.ReplaceableProgress
            && row.DispatchRef == SlackStatusProjection.DispatchRef(
                new SlackMessageIdentity("T123", "C-THREAD", "1710000000.000450"), "progress"));
        var payload = SlackDeliveryPayload.Parse(progress.PayloadJson);
        var sessionId = result.GetProperty("sessionId").GetString();
        Assert.Contains($"Session: {sessionId}", payload.Text, StringComparison.Ordinal);
        Assert.Contains(sessionId!, payload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Working", payload.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Backpressured_new_dm_uses_adapter_owned_fallback_without_a_nudge()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.AgentConnections
                .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.ConnectionHealth, ConnectionHealthKind.Degraded)
                    .SetProperty(row => row.HealthReason, SlackProviderBackpressureReasons.OutboxOverflow));
        }

        var result = await PostIngressAsync(connection, "D-DM-BUSY", "1710000000.001500", "please retry");

        Assert.Equal("backpressured", result.GetProperty("kind").GetString());
        Assert.Equal("adapter", result.GetProperty("responseOwner").GetString());
        Assert.Equal(SlackAdmissionMessages.Backpressured, result.GetProperty("reason").GetString());
        Assert.Empty(await GetAdmissionNudgesAsync(connection, "D-DM-BUSY"));

        await using var verify = _fixture.Services.CreateAsyncScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await dbVerify.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "D-DM-BUSY")
            .ToListAsync());
        Assert.Empty(await dbVerify.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id
                && row.LabelSlackConversationId == "D-DM-BUSY")
            .ToListAsync());
    }

    private async Task<JsonElement> PostIngressAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string text,
        string? threadTs = null)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            apiAppId = "A123",
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            senderSlackUserId = "U_OWNER",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task RegisterRunnerAsync(string projectId, string runnerId)
    {
        // The MohistIntegration collection shares one runner registry across
        // classes. Drain stale registrations so this proof claims its own job.
        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await _fixture.Grains.GetGrain<IRunnerGrain>(staleId).UnregisterAsync();
        Assert.Empty(await registry.ListRunnerIdsAsync());

        using var register = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId,
        });
        register.EnsureSuccessStatusCode();
        _runnerIds.Add(runnerId);
        using var slots = await _fixture.Client.PatchAsJsonAsync($"/api/runner/{runnerId}", new { slots = 1 });
        slots.EnsureSuccessStatusCode();
    }

    private async Task<ClaimResult> AcceptLaunchAsync(
        string jobKey,
        string runnerId,
        string projectId)
    {
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
        await _fixture.AgentJobDispatches.WaitForAssignmentPreparedAsync(
            jobKey,
            TimeSpan.FromSeconds(5));

        var assignment = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(runnerId, assignment.RunnerId);
        Assert.Equal(AgentJobStatus.Pending, assignment.Status);
        Assert.False(string.IsNullOrWhiteSpace(assignment.CurrentWorkId));

        var claim = await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId)
            .TryClaimAgentJobAsync(jobKey, projectId);
        Assert.NotNull(claim);
        Assert.Equal(jobKey, claim.AgentJobId);
        Assert.Equal(runnerId, claim.RunnerId);
        Assert.Equal(assignment.CurrentWorkId, claim.WorkId);
        return claim;
    }

    private async Task AssertReceivedProjectionAsync(AgentConnection connection, string conversationId, string messageTs)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var payload = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.DispatchRef == SlackStatusProjection.DispatchRef(
                    new SlackMessageIdentity("T123", conversationId, messageTs), "received"))
            .Select(row => row.PayloadJson)
            .SingleAsync();
        Assert.Equal(SlackDeliveryOperations.ReactionAdd, SlackDeliveryPayload.Parse(payload).Operation);
    }


    private async Task SetAgentConfigAsync(AgentConnection connection, object? config)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Agents.SingleAsync(agent => agent.ProjectId == connection.ProjectId);
        var agent = new Mohist.Server.Agent.Domain.Agent
        {
            Id = row.Id,
            ProjectId = connection.ProjectId,
            Name = "Mohist Agent",
            Status = AgentStatus.Active,
            Instructions = "Handle Slack requests.",
            AgentConfig = config is null ? null : JsonSerializer.SerializeToElement(config),
        };
        row.State = JsonSerializer.Serialize(agent, JSON.Options);
        await db.SaveChangesAsync();
    }

    private async Task<List<SlackOutboxRow>> GetAdmissionNudgesAsync(
        AgentConnection connection,
        string conversationId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return (await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.DispatchRef != null)
            .ToListAsync())
            .Where(row => row.DispatchRef!.StartsWith("slack-admission-nudge:", StringComparison.Ordinal))
            .ToList();
    }

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var seeded = await SlackManagedConnectionSeed.CreateAsync(_fixture, new SlackSeedOptions
        {
            // The stale connection-address tokens are the scenario: rotation
            // must prefer the managed-app credentials for the runtime lease.
            ConnectionAppToken = "xapp-old",
            ConnectionBotToken = "xoxb-old",
        });
        _connectionLeases[seeded.Connection.Id] = seeded.LeaseId;
        return seeded.Connection;
    }

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";
}
