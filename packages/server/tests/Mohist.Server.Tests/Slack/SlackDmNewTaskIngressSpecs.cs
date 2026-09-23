using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        Assert.Equal($"Session: {firstSessionId}", Assert.NotNull(firstPayload.Blocks)[0].GetProperty("text").GetProperty("text").GetString());
        Assert.Equal($"Session: {secondSessionId}", Assert.NotNull(secondPayload.Blocks)[0].GetProperty("text").GetProperty("text").GetString());
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

    [Theory]
    [InlineData(null)]
    [InlineData("https://mohist.example/base")]
    public async Task Established_ordinary_dm_followup_bypasses_the_new_work_gate(string? externalWebUrl)
    {
        var options = _fixture.Services.GetRequiredService<IOptions<SlackProviderOptions>>().Value;
        var previousUrl = options.ExternalWebUrl;
        options.ExternalWebUrl = externalWebUrl;
        try
        {
            var connection = await CreateConnectionAsync();
            var initial = await PostIngressAsync(connection, "D-DM-FOLLOWUP-READY", "1710000000.001300", "initial task");
            var sessionId = initial.GetProperty("sessionId").GetString()!;
            await SetAgentConfigAsync(connection, null);

            var followup = await PostIngressAsync(
                connection,
                "D-DM-FOLLOWUP-READY",
                "1710000000.001400",
                "ordinary follow-up");

            Assert.True(followup.GetProperty("followup").GetBoolean());
            Assert.Equal(sessionId, followup.GetProperty("sessionId").GetString());
            Assert.Empty(await GetAdmissionNudgesAsync(connection, "D-DM-FOLLOWUP-READY"));

            await using var scope = _fixture.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
            var cards = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries
                .Where(row => row.Kind == SlackOutboxKinds.ReplaceableProgress).ToArray();
            Assert.Equal(2, cards.Length);
            foreach (var card in cards)
                AssertSessionCard(SlackDeliveryPayload.Parse(card.PayloadJson), connection.ProjectId, sessionId, externalWebUrl);
            var followupCard = Assert.Single(cards, row => row.DispatchRef!.StartsWith($"agent-session-followup:{sessionId}:", StringComparison.Ordinal));
            var providerIdentity = new SlackProviderMessageIdentity("D-DM-FOLLOWUP-READY", "1710000000.001401");
            await outbox.MarkDeliveredAsync(connection.ProjectId, followupCard.Id, providerIdentity);

            var replay = await PostIngressAsync(connection, "D-DM-FOLLOWUP-READY", "1710000000.001400", "ordinary follow-up");
            Assert.Equal(sessionId, replay.GetProperty("sessionId").GetString());
            var reply = await outbox.EnqueueAgentReplyAsync(
                connection.ProjectId, "D-DM-FOLLOWUP-READY", followupCard.ThreadTs!,
                "The Agent's answer.", connection.Id, followupCard.DispatchRef!);
            Assert.True(reply.Accepted);
            await scope.ServiceProvider.GetRequiredService<SlackStatusProjection>().EnqueueFailureAsync(
                connection.ProjectId, connection.Id,
                new SlackMessageIdentity(connection.WorkspaceTeamId, "D-DM-FOLLOWUP-READY", "1710000000.001400"),
                followupCard.ThreadTs, "System delivery failed.");
            var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
            var terminal = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.TerminalResult);
            Assert.Equal(reply.DeliveryId, terminal.Id);
            Assert.NotEqual(followupCard.Id, terminal.Id);
            Assert.Equal("The Agent's answer.", SlackDeliveryPayload.Parse(terminal.PayloadJson).Text);
            var failure = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.ExplicitFailure);
            Assert.NotEqual(followupCard.Id, failure.Id);
            Assert.NotEqual(terminal.Id, failure.Id);
            Assert.Null(SlackDeliveryPayload.Parse(terminal.PayloadJson).ProviderMessageIdentity);
            Assert.Null(SlackDeliveryPayload.Parse(failure.PayloadJson).ProviderMessageIdentity);
            var replayedCards = rows
                .Where(row => row.Kind == SlackOutboxKinds.ReplaceableProgress).ToArray();
            Assert.Equal(2, replayedCards.Length);
            var replayed = Assert.Single(replayedCards, row => row.DispatchRef == followupCard.DispatchRef);
            Assert.Equal(followupCard.Id, replayed.Id);
            Assert.Equal(SlackOutboxStates.Delivered, replayed.State);
            var replayedPayload = SlackDeliveryPayload.Parse(replayed.PayloadJson);
            Assert.Equal(providerIdentity, replayedPayload.ProviderMessageIdentity);
            Assert.Equal(SlackDeliveryPayload.Parse(followupCard.PayloadJson).Blocks?.GetRawText(), replayedPayload.Blocks?.GetRawText());
        }
        finally
        {
            options.ExternalWebUrl = previousUrl;
        }
    }

    [Fact]
    public async Task Established_dm_followup_is_rejected_with_a_visible_reply_when_the_launch_never_bound_a_runtime_session()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = $"slack-dm-unbound-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(connection.ProjectId, runnerId);

        var initial = await PostIngressAsync(connection, "D-DM-UNBOUND", "1710000000.001700", "initial task");
        var sessionId = initial.GetProperty("sessionId").GetString()!;
        var jobKey = initial.GetProperty("jobKey").GetString()!;
        var claim = await AcceptLaunchAsync(jobKey, runnerId, connection.ProjectId);

        // The Runner dies before attaching a physical runtime session, so the
        // launch turn goes terminal with no binding — the black hole this
        // contract closes. Follow-ups must not park in an invisible queue.
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
        await job.ReportResultAsync(
            runnerId,
            claim.WorkId,
            new WorkResult("failed", "The bound runtime is disabled on the Runner."));
        await job.WaitForTerminalAsync();

        var followup = await PostIngressAsync(connection, "D-DM-UNBOUND", "1710000000.001800", "ordinary follow-up");

        Assert.Equal("runtime_session_missing", followup.GetProperty("kind").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());
        Assert.Equal("server", followup.GetProperty("responseOwner").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var expectedDispatchRef = $"slack-followup-rejected:{connection.WorkspaceTeamId}/D-DM-UNBOUND/1710000000.001800";
        var rejection = await db.SlackOutboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.ConversationId == "D-DM-UNBOUND"
            && row.DispatchRef == expectedDispatchRef);
        Assert.Equal(SlackOutboxKinds.UserAction, rejection.Kind);
        Assert.Contains("cannot continue automatically", rejection.PayloadJson, StringComparison.Ordinal);

        var inbox = await db.SlackProviderInboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.SlackMessageIdentity == $"{connection.WorkspaceTeamId}/D-DM-UNBOUND/1710000000.001800");
        Assert.NotNull(inbox.DispatchedAt);
        Assert.Equal(sessionId, await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>()
            .GetCurrentSessionIdAsync(connection.ProjectId, connection.Id, "D-DM-UNBOUND", default));
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
        Assert.Equal($"Session: {sessionId}", Assert.NotNull(payload.Blocks)[0].GetProperty("text").GetProperty("text").GetString());
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

    private static void AssertSessionCard(SlackDeliveryPayload payload, string projectName, string sessionId, string? externalWebUrl)
    {
        Assert.Equal($"Agent session.\nSession: {sessionId}", payload.Text);
        Assert.DoesNotContain("Working", payload.Text, StringComparison.OrdinalIgnoreCase);
        var blocks = Assert.NotNull(payload.Blocks);
        Assert.Equal("section", blocks[0].GetProperty("type").GetString());
        Assert.Equal("plain_text", blocks[0].GetProperty("text").GetProperty("type").GetString());
        Assert.Equal($"Session: {sessionId}", blocks[0].GetProperty("text").GetProperty("text").GetString());
        var sections = blocks.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "section").ToArray();
        Assert.Equal(externalWebUrl is null ? 1 : 2, sections.Length);
        if (externalWebUrl is not null)
        {
            Assert.Equal("mrkdwn", sections[1].GetProperty("text").GetProperty("type").GetString());
            Assert.Equal($"<{externalWebUrl}/{projectName}/sessions/{sessionId}|Open in Mohist>",
                sections[1].GetProperty("text").GetProperty("text").GetString());
            Assert.Equal(new[] { "text", "type" }, sections[1].EnumerateObject().Select(property => property.Name).Order());
        }
        var action = Assert.Single(blocks.EnumerateArray(), block => block.GetProperty("type").GetString() == "actions");
        var button = Assert.Single(action.GetProperty("elements").EnumerateArray());
        Assert.Equal(SlackTurnControlService.StopActionId, button.GetProperty("action_id").GetString());
        var stop = JSON.Deserialize<SlackStopActionPayload>(button.GetProperty("value").GetString()!);
        Assert.NotNull(stop);
        Assert.Equal(sessionId, stop.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(stop.Signature));
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
            Id = connection.AgentId,
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
