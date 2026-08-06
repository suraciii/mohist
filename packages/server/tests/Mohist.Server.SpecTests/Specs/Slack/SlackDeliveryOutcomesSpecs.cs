using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackDeliveryOutcomesSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackDeliveryOutcomesSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Delivery_outcome_retry_reschedules_explicit_failure_without_marking_uncertain()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var queued = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_42",
            "{\"text\":\"reply\"}"));
        await outbox.MarkDeliveredAsync(connection.ProjectId, queued.Id);
        await outbox.ScheduleRetryAsync(connection.ProjectId, queued.Id, "channel_not_found");

        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await verifyDb.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.Id == queued.Id);
        Assert.Equal(SlackOutboxStates.Pending, row.State);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal("channel_not_found", row.LastError);
    }

    [Fact]
    public async Task ResendUncertain_only_advances_delivery_uncertain_rows()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var pending = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_pending",
            "{\"text\":\"still pending\"}"));
        var uncertain = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_uncertain",
            "{\"text\":\"uncertain\"}"));
        await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, uncertain.Id, "claim timeout");

        var resendResult = await outbox.ResendUncertainAsync(connection.ProjectId, connection.Id, uncertain.Id);
        Assert.Equal(1, resendResult);

        var notUncertainResult = await outbox.ResendUncertainAsync(connection.ProjectId, connection.Id, pending.Id);
        Assert.Equal(0, notUncertainResult);

        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var advanced = await verifyDb.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.Id == uncertain.Id);
        Assert.Equal(SlackOutboxStates.Pending, advanced.State);
        Assert.Null(advanced.DeliveryUncertainAt);

        var untouched = await verifyDb.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.Id == pending.Id);
        Assert.Equal(SlackOutboxStates.Pending, untouched.State);
    }

    [Fact]
    public async Task List_deliveries_returns_all_rows_with_state_and_reason()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var uncertain = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_uncertain",
            "{\"text\":\"uncertain\"}"));
        await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, uncertain.Id, "claim timeout");

        using var response = await _fixture.Client.GetAsync(Path(connection, "/deliveries"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = document.RootElement.GetProperty("data").GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        var entry = entries[0];
        Assert.Equal(uncertain.Id, entry.GetProperty("id").GetString());
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, entry.GetProperty("state").GetString());
        Assert.Equal("claim timeout", entry.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task Resend_endpoint_transitions_uncertain_to_pending_without_touching_execution_result()
    {
        var connection = await CreateConnectionAsync();
        var dispatchRef = "agentjob_42";
        string queuedDeliveryId;
        await using (var seedScope = _fixture.Services.CreateAsyncScope())
        {
            var outbox = seedScope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
            var queued = await outbox.EnqueueAsync(new SlackOutboxDraft(
                connection.ProjectId,
                connection.Id,
                connection.WorkspaceTeamId,
                "D1",
                SlackOutboxKinds.TerminalResult,
                dispatchRef,
                "{\"text\":\"reply\"}"));
            await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, queued.Id, "claim timeout");
            queuedDeliveryId = queued.Id;

            var db = seedScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var stateJson = $"{{\"input\":{{\"projectId\":\"{connection.ProjectId}\",\"agentId\":\"agent-1\"}},\"status\":\"{nameof(AgentJobStatus.Completed)}\",\"submittedAt\":\"{_fixture.TimeProvider.GetUtcNow():O}\"}}";
            var jobRow = new AgentJobRow
            {
                JobKey = dispatchRef,
                State = stateJson,
                Status = nameof(AgentJobStatus.Completed),
            };
            db.AgentJobs.Add(jobRow);
            await db.SaveChangesAsync();
        }

        using var resend = await _fixture.Client.PostAsync(
            Path(connection, $"/deliveries/{queuedDeliveryId}/resend"), content: null);
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);

        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await verifyDb.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.Id == queuedDeliveryId);
        Assert.Equal(SlackOutboxStates.Pending, row.State);

        var job = await verifyDb.AgentJobs.AsNoTracking()
            .SingleAsync(j => j.JobKey == dispatchRef);
        Assert.Equal(nameof(AgentJobStatus.Completed), job.Status);
    }

    [Fact]
    public async Task Resend_endpoint_rejects_non_uncertain_row_with_409()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var pending = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_pending",
            "{\"text\":\"pending\"}"));

        using var resend = await _fixture.Client.PostAsync(
            Path(connection, $"/deliveries/{pending.Id}/resend"), content: null);

        Assert.Equal(HttpStatusCode.Conflict, resend.StatusCode);
    }

    [Fact]
    public async Task Agent_reply_promotes_the_liveness_progress_message_in_place()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var projection = scope.ServiceProvider.GetRequiredService<SlackStatusProjection>();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-reply-inplace", "1710000000.000010");
        var working = await projection.EnqueueWorkingAsync(connection.ProjectId, connection.Id, source, threadTs: null);

        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "C-reply-inplace", null, "All green — the task is complete.");

        Assert.True(reply.Accepted);
        Assert.Equal(connection.Id, reply.ConnectionId);
        Assert.Equal(working.Id, reply.DeliveryId);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        var terminal = Assert.Single(rows, r => r.Kind == SlackOutboxKinds.TerminalResult);
        Assert.Equal(working.Id, terminal.Id);
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Contains("the task is complete", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_reply_merges_repeated_sends_into_one_terminal_answer()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var projection = scope.ServiceProvider.GetRequiredService<SlackStatusProjection>();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-reply-merge", "1710000000.000020");
        await projection.EnqueueWorkingAsync(connection.ProjectId, connection.Id, source, threadTs: null);

        var first = await outbox.EnqueueAgentReplyAsync(connection.ProjectId, "C-reply-merge", null, "part one");
        var second = await outbox.EnqueueAgentReplyAsync(connection.ProjectId, "C-reply-merge", null, "part two");

        Assert.True(first.Accepted);
        Assert.Equal(first.DeliveryId, second.DeliveryId);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        var terminal = Assert.Single(rows, r => r.Kind == SlackOutboxKinds.TerminalResult);
        var payload = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        Assert.Contains("part one", payload.Text, StringComparison.Ordinal);
        Assert.Contains("part two", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_reply_promotion_carries_liveness_status_ref_so_finalization_locates_the_reaction_target()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var projection = scope.ServiceProvider.GetRequiredService<SlackStatusProjection>();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-reply-statusref", "1710000000.000030");
        var progressDispatchRef = "agent-session-followup:session-statusref:turn-1:progress";
        await projection.EnqueueWorkingAsync(connection.ProjectId, connection.Id, source, threadTs: null, progressDispatchRef);

        var reply = await outbox.EnqueueAgentReplyAsync(connection.ProjectId, "C-reply-statusref", null, "the answer");

        Assert.True(reply.Accepted);
        var promoted = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries
            .Single(row => row.Kind == SlackOutboxKinds.TerminalResult);
        // The in-place promotion must carry the liveness StatusDispatchRef so the
        // post-reply liveness finalization can derive the reaction target from the
        // authoritative progress-row metadata, not from a potentially-wrong delivery source.
        Assert.Equal(SlackStatusProjection.DispatchRef(source, "status"),
            SlackDeliveryPayload.Parse(promoted.PayloadJson).StatusDispatchRef);

        // Simulate the terminal handler's source differing from the ingress source
        // (e.g. delivery.MessageTs null -> synthetic ts). FinalizeLivenessAsync must
        // target the ORIGINAL message (from StatusDispatchRef), not the synthetic one —
        // otherwise the reaction mutation targets a non-existent message and stalls.
        var deliverySource = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-reply-statusref", "terminal:synthetic");
        await projection.FinalizeLivenessAsync(
            connection.ProjectId, connection.Id, deliverySource, threadTs: null, "completed", progressDispatchRef);

        var finalized = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        Assert.Contains(finalized, row => row.Kind == SlackOutboxKinds.ReactionMutation
            && row.DispatchRef == SlackStatusProjection.DispatchRef(source, "terminal-add"));
        Assert.DoesNotContain(finalized, row => row.Kind == SlackOutboxKinds.ReactionMutation
            && row.DispatchRef == SlackStatusProjection.DispatchRef(deliverySource, "terminal-add"));
    }

    [Fact]
    public async Task Agent_reply_without_an_active_conversation_is_not_accepted()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();

        var reply = await outbox.EnqueueAgentReplyAsync(connection.ProjectId, "C-reply-unknown", null, "hello");

        Assert.False(reply.Accepted);
    }

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
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
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = "agent-1",
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
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
        };
    }

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";
}