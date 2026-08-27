using System.Net;
using System.Net.Http.Json;
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
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("SlackApiSurface")]
public sealed class SlackDmNewTaskIngressSpecs : IAsyncLifetime
{
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);
    private readonly MohistIntegrationFixture _fixture;
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
        var first = await PostIngressAsync(connection, "D-DM-NEW", "1710000000.000100", "first task");
        var firstSessionId = first.GetProperty("sessionId").GetString();
        var firstJobKey = first.GetProperty("jobKey").GetString();

        var second = await PostIngressAsync(connection, "D-DM-NEW", "1710000000.000200", "new task second task");
        var secondSessionId = second.GetProperty("sessionId").GetString();
        var secondJobKey = second.GetProperty("jobKey").GetString();

        Assert.True(second.GetProperty("newTask").GetBoolean());
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
        Assert.Equal("Working...", firstPayload.Text);
        Assert.Equal("Working...", secondPayload.Text);
        Assert.Contains(SlackTurnControlService.StopActionId, firstPayload.Blocks?.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(SlackTurnControlService.StopActionId, secondPayload.Blocks?.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-", firstProgress.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-", secondProgress.PayloadJson, StringComparison.Ordinal);
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
        var firstDispatch = await AcceptLaunchAsync(firstJobKey, runnerId, connection.ProjectId);
        var firstJob = _fixture.Grains.GetGrain<IAgentJobGrain>(firstJobKey);
        Assert.Equal(AgentJobStatus.Running, await firstJob.GetStatusAsync());

        var second = await PostIngressAsync(connection, "D-DM-RUNNING", "1710000000.000400", "new task independent task");
        Assert.True(second.GetProperty("newTask").GetBoolean());
        Assert.Equal(AgentJobStatus.Running, await firstJob.GetStatusAsync());

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
        Assert.Equal("Working...", SlackDeliveryPayload.Parse(progress.PayloadJson).Text);
    }

    [Fact]
    public async Task Unconfigured_new_dm_gets_one_safe_durable_nudge_without_execution_side_effects()
    {
        var connection = await CreateConnectionAsync();
        await SetAgentConfigAsync(connection, null);

        var first = await PostIngressAsync(
            connection,
            "D-DM-SETUP",
            "1710000000.001000",
            "please do this");
        var replay = await PostIngressAsync(
            connection,
            "D-DM-SETUP",
            "1710000000.001000",
            "please do this");

        Assert.Equal("agent_not_configured", first.GetProperty("kind").GetString());
        Assert.Equal("server", first.GetProperty("responseOwner").GetString());
        Assert.Equal("server", replay.GetProperty("responseOwner").GetString());
        Assert.Equal(SlackAdmissionMessages.AgentNotReady, first.GetProperty("reason").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var nudges = (await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "D-DM-SETUP"
                && row.DispatchRef != null)
            .ToListAsync())
            .Where(row => row.DispatchRef!.StartsWith("slack-admission-nudge:", StringComparison.Ordinal))
            .ToList();
        var nudge = Assert.Single(nudges);
        Assert.Equal("D-DM-SETUP", nudge.ConversationId);
        Assert.Null(nudge.ThreadTs);
        var payload = SlackDeliveryPayload.Parse(nudge.PayloadJson);
        Assert.Equal(SlackAdmissionMessages.AgentNotReady, payload.Text);
        Assert.Equal(nudge.DispatchRef, payload.ClientMessageId);
        Assert.True(nudge.DispatchRef!.Length <= 256);
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "D-DM-SETUP")
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id)
            .ToListAsync());
        Assert.Empty(await db.AgentJobs
            .Where(row => row.ProjectId == connection.ProjectId)
            .ToListAsync());
        Assert.DoesNotContain("xoxb-", nudge.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", nudge.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redelivery_after_uncertain_delivery_keeps_the_original_server_owned_nudge()
    {
        var connection = await CreateConnectionAsync();
        await SetAgentConfigAsync(connection, null);

        var first = await PostIngressAsync(
            connection,
            "D-DM-UNCERTAIN",
            "1710000000.001050",
            "please do this");
        Assert.Equal("server", first.GetProperty("responseOwner").GetString());

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
            var row = Assert.Single(await GetAdmissionNudgesAsync(connection, "D-DM-UNCERTAIN"));
            var claimed = await outbox.ClaimAsync(connection.ProjectId, connection.Id, SlackRuntimeLeaseTestSupport.AdapterId);
            Assert.Equal(row.Id, claimed?.Id);
            await outbox.MarkDeliveryUncertainAsync(
                connection.ProjectId,
                row.Id,
                "provider response lost",
                SlackRuntimeLeaseTestSupport.AdapterId);
        }

        var replay = await PostIngressAsync(
            connection,
            "D-DM-UNCERTAIN",
            "1710000000.001050",
            "please do this");
        Assert.Equal("server", replay.GetProperty("responseOwner").GetString());
        Assert.Equal("agent_not_configured", replay.GetProperty("kind").GetString());

        var rows = await GetAdmissionNudgesAsync(connection, "D-DM-UNCERTAIN");
        var original = Assert.Single(rows);
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, original.State);
        Assert.Equal(SlackAdmissionService.DispatchRef(
            connection,
            new SlackMessageIdentity("T123", "D-DM-UNCERTAIN", "1710000000.001050")), original.DispatchRef);
        Assert.Equal(original.DispatchRef, SlackDeliveryPayload.Parse(original.PayloadJson).ClientMessageId);
    }

    [Fact]
    public async Task Explicit_new_task_is_gated_before_the_existing_dm_session_mapping()
    {
        var connection = await CreateConnectionAsync();
        var initial = await PostIngressAsync(connection, "D-DM-MARKER", "1710000000.001100", "initial task");
        var currentSessionId = initial.GetProperty("sessionId").GetString();
        await SetAgentConfigAsync(connection, null);

        var blocked = await PostIngressAsync(
            connection,
            "D-DM-MARKER",
            "1710000000.001200",
            "new task independent work");

        Assert.Equal("agent_not_configured", blocked.GetProperty("kind").GetString());
        Assert.Equal("server", blocked.GetProperty("responseOwner").GetString());
        Assert.DoesNotContain(blocked.EnumerateObject(), property => property.Name is "sessionId" or "inputId" or "turnId");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Equal(currentSessionId, await db.SlackDmSessionMappings
            .Where(row => row.ConnectionId == connection.Id && row.DmConversationId == "D-DM-MARKER")
            .Select(row => row.CurrentSessionId)
            .SingleAsync());
        Assert.DoesNotContain(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "D-DM-MARKER")
            .Select(row => row.SlackMessageIdentity)
            .ToListAsync(), identity => identity.EndsWith("1710000000.001200", StringComparison.Ordinal));
        var markerNudges = (await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.DispatchRef != null
                && row.ConversationId == "D-DM-MARKER")
            .ToListAsync())
            .Where(row => row.DispatchRef!.StartsWith("slack-admission-nudge:", StringComparison.Ordinal))
            .ToList();
        Assert.Single(markerNudges);
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
        Assert.DoesNotContain(await GetAdmissionNudgesAsync(connection, "D-DM-FOLLOWUP-READY"),
            row => row.DispatchRef!.Contains("1710000000.001400", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unavailable_enabled_connection_gets_a_thread_anchored_nudge()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.AgentConnections
                .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.ConnectionHealth, ConnectionHealthKind.Unhealthy)
                    .SetProperty(row => row.HealthReason, "Slack service is offline."));
        }

        var result = await PostIngressAsync(
            connection,
            "D-DM-UNAVAILABLE",
            "1710000000.001450",
            "please retry this",
            "1710000000.001400");

        Assert.Equal("connection_unavailable", result.GetProperty("kind").GetString());
        Assert.Equal("server", result.GetProperty("responseOwner").GetString());
        Assert.Equal(SlackAdmissionMessages.ConnectionUnavailable, result.GetProperty("reason").GetString());

        var nudges = await GetAdmissionNudgesAsync(connection, "D-DM-UNAVAILABLE");
        var nudge = Assert.Single(nudges);
        Assert.Equal("1710000000.001400", nudge.ThreadTs);
        var payload = SlackDeliveryPayload.Parse(nudge.PayloadJson);
        Assert.Equal(nudge.DispatchRef, payload.ClientMessageId);
        Assert.Equal(SlackAdmissionMessages.ConnectionUnavailable, payload.Text);
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
    }

    [Fact]
    public async Task Diagnostic_exposes_canonical_executability_gaps_for_operators()
    {
        var connection = await CreateConnectionAsync();
        await SetAgentConfigAsync(connection, null);

        using var response = await _fixture.Client.GetAsync(Path(connection, "/diagnostic"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        var executability = data.GetProperty("facts").GetProperty("agentExecutability");
        Assert.Equal(AgentExecutabilityStates.NotConfigured, executability.GetProperty("state").GetString());
        var gap = Assert.Single(executability.GetProperty("gaps").EnumerateArray());
        Assert.Equal("model-missing", gap.GetProperty("code").GetString());
        Assert.Equal("Set a model in Agent settings.", gap.GetProperty("nextAction").GetString());
        Assert.Equal(ConnectionDiagnosticState.AgentNeedsSetup, data.GetProperty("primaryState").GetString());
        Assert.Equal("Review the Agent execution settings.", data.GetProperty("nextAction").GetString());
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

    private async Task<JsonElement> PostIngressAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string text,
        string? threadTs = null)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
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
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task RegisterRunnerAsync(string projectId, string runnerId)
    {
        // The MohistIntegration collection shares one silo and one runner
        // registry across every class. A runner left behind by an earlier
        // class (e.g. a silently-failed unregister) would win this job's
        // admission — ListEligibleRunnersAsync returns every registered
        // runner regardless of projectId — and this test's own runner would
        // never receive the dispatch. Drain the registry so admission
        // deterministically selects the runner registered here.
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

        // Slack ingress owns the job lifecycle assertion. Claim through the
        // registered fake runner's authoritative grain boundary so this spec
        // does not depend on /poll candidate enumeration converging first.
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
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
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
                Instructions = "Handle Slack requests.",
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

        var agentAppId = $"agent_app_{Guid.NewGuid():N}";
        var enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(_fixture, "T123");
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = "T123",
            AgentConnectionId = id,
            AppId = $"A_SPEC_{Guid.NewGuid():N}",
            BotUserId = "U123",
            AppLifecycle = SlackAppLifecycle.Created,
            Authorization = SlackAuthorizationState.Authorized,
            RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
            DesiredManifestVersion = 1,
            DesiredManifestHash = "desired",
            VerifiedScopesJson = "[]",
            OperationFence = 0,
            AppLevelTokenRef = agentAppId,
            BotTokenRef = agentAppId,
            BindingState = SlackAgentAppBindingState.Bound,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-old"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-old"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, projectId, id);
        _connectionLeases[id] = leaseId;
        return new AgentConnection { Id = id, ProjectId = projectId, WorkspaceTeamId = "T123" };
    }

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";
}
