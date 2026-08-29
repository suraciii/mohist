using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackDeliveryOutcomesSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, ReplyAnchor> _replyAnchors = new(StringComparer.Ordinal);

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
    public async Task Agent_reply_dispatch_identity_separates_turns_and_keeps_retries_idempotent()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-reply-turns");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();

        var first = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "ACK",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var retry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "ACK",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var providerIdentity = new SlackProviderMessageIdentity("D-reply-turns", "1710000000.000500");
        await outbox.MarkDeliveredAsync(connection.ProjectId, first.DeliveryId!, providerIdentity);
        var deliveredRetry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "ACK",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var deliveredDistinct = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "more detail",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var latestRetry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "more detail",
            replyDispatchRef: "agent-session-followup:session-1:turn-1");
        var secondTurn = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-turns",
            null,
            "531",
            replyDispatchRef: "agent-session-followup:session-1:turn-2");

        Assert.Equal(first.DeliveryId, retry.DeliveryId);
        Assert.Equal(first.DeliveryId, deliveredRetry.DeliveryId);
        Assert.Equal(first.DeliveryId, deliveredDistinct.DeliveryId);
        Assert.Equal(first.DeliveryId, latestRetry.DeliveryId);
        Assert.NotEqual(first.DeliveryId, secondTurn.DeliveryId);
        Assert.False(secondTurn.MergedIntoExisting);

        var replies = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries
            .Where(row => row.Kind == SlackOutboxKinds.TerminalResult)
            .ToArray();
        Assert.Equal(2, replies.Length);
        var firstReply = Assert.Single(
            replies,
            row => row.DispatchRef == "slack-reply:agent-session-followup:session-1:turn-1:terminal");
        var secondReply = Assert.Single(
            replies,
            row => row.DispatchRef == "slack-reply:agent-session-followup:session-1:turn-2:terminal");
        Assert.Equal(SlackOutboxStates.Pending, firstReply.State);
        var firstPayload = SlackDeliveryPayload.Parse(firstReply.PayloadJson);
        Assert.Equal("ACK\n\nmore detail", firstPayload.Text);
        Assert.Equal(["ACK", "more detail"], firstPayload.ReplyParts);
        Assert.Equal(firstPayload.Text, firstPayload.FallbackText);
        Assert.Equal(SlackDeliveryOperations.ChatUpdate, firstPayload.Operation);
        Assert.Equal(providerIdentity, firstPayload.ProviderMessageIdentity);
        Assert.Equal("531", SlackDeliveryPayload.Parse(secondReply.PayloadJson).Text);
    }

    [Fact]
    public async Task Anchored_retry_recognizes_the_latest_part_of_a_pre_reply_parts_payload()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-reply-legacy-parts");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        const string dispatchRef = "agent-session-followup:legacy-parts:turn-1";

        await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "D-reply-legacy-parts", null, "part one", replyDispatchRef: dispatchRef);
        await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "D-reply-legacy-parts", null, "part two", replyDispatchRef: dispatchRef);

        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.SlackOutboxRows.SingleAsync(candidate =>
            candidate.ProjectId == connection.ProjectId && candidate.ConversationId == "D-reply-legacy-parts");
        row.PayloadJson = JsonSerializer.Serialize(SlackDeliveryPayload.Parse(row.PayloadJson) with { ReplyParts = null });
        await db.SaveChangesAsync();

        var retry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId, "D-reply-legacy-parts", null, "part two", replyDispatchRef: dispatchRef);

        Assert.True(retry.Accepted);
        var persisted = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        Assert.Equal("part one\n\npart two", SlackDeliveryPayload.Parse(persisted.PayloadJson).Text);
    }

    [Fact]
    public async Task Authoritative_reply_parts_do_not_treat_a_paragraph_suffix_as_a_retry()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-reply-authoritative-parts");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        const string dispatchRef = "agent-session-followup:authoritative-parts:turn-1";

        await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-authoritative-parts",
            null,
            "summary\n\nnext",
            replyDispatchRef: dispatchRef);
        await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-authoritative-parts",
            null,
            "next",
            replyDispatchRef: dispatchRef);

        var row = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Equal("summary\n\nnext\n\nnext", payload.Text);
        Assert.Equal(["summary\n\nnext", "next"], payload.ReplyParts);
    }

    [Fact]
    public async Task Explicit_reply_reuses_identical_in_flight_retry_and_rejects_different_content()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-reply-in-flight");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        const string replyDispatchRef = "agent-session-followup:session-1:turn-in-flight";

        var first = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "original",
            replyDispatchRef: replyDispatchRef);
        var claimed = await outbox.ClaimAsync(connection.ProjectId, connection.Id, "adapter-in-flight");
        Assert.Equal(first.DeliveryId, claimed?.Id);

        var claimedRetry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "original",
            replyDispatchRef: replyDispatchRef);
        var claimedAddition = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "claimed addition",
            replyDispatchRef: replyDispatchRef);
        Assert.True(claimedRetry.Accepted);
        Assert.False(claimedAddition.Accepted);
        Assert.True(claimedAddition.ConflictingDuplicate);
        var claimedRow = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        Assert.Equal(SlackOutboxStates.Claimed, claimedRow.State);
        Assert.Equal("original", SlackDeliveryPayload.Parse(claimedRow.PayloadJson).Text);

        await outbox.MarkDeliveryUncertainAsync(
            connection.ProjectId, claimedRow.Id, "provider response lost", "adapter-in-flight");
        var uncertainRetry = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "original",
            replyDispatchRef: replyDispatchRef);
        var uncertainAddition = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-reply-in-flight",
            null,
            "uncertain addition",
            replyDispatchRef: replyDispatchRef);
        Assert.True(uncertainRetry.Accepted);
        Assert.False(uncertainAddition.Accepted);
        Assert.True(uncertainAddition.ConflictingDuplicate);
        var uncertainRow = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, uncertainRow.State);
        Assert.Equal("original", SlackDeliveryPayload.Parse(uncertainRow.PayloadJson).Text);
    }

    [Fact]
    public async Task Anchored_agent_reply_promotes_only_the_requested_connection_progress()
    {
        var first = await CreateConnectionAsync();
        var second = await CreateConnectionAsync(first.ProjectId, "agent-2");
        await CreateThreadMappingAsync(first, "C-shared-progress", "1710000000.000025");
        await CreateThreadMappingAsync(second, "C-shared-progress", "1710000000.000025");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var projection = scope.ServiceProvider.GetRequiredService<SlackStatusProjection>();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var source = new SlackMessageIdentity(first.WorkspaceTeamId, "C-shared-progress", "1710000000.000026");
        var firstProgress = await projection.EnqueueWorkingAsync(
            first.ProjectId, first.Id, source, "1710000000.000025", "first:progress");
        var secondProgress = await projection.EnqueueWorkingAsync(
            second.ProjectId, second.Id, source, "1710000000.000025", "second:progress");

        var reply = await outbox.EnqueueAgentReplyAsync(
            first.ProjectId,
            "C-shared-progress",
            "1710000000.000025",
            "second answer",
            connectionId: second.Id,
            replyDispatchRef: "second:turn");

        Assert.True(reply.Accepted);
        Assert.Equal(second.Id, reply.ConnectionId);
        Assert.Equal(secondProgress.Id, reply.DeliveryId);
        var firstRows = (await outbox.ListAsync(first.ProjectId, first.Id)).Entries;
        Assert.Contains(firstRows, row => row.Id == firstProgress.Id
            && row.Kind == SlackOutboxKinds.ReplaceableProgress);
        var secondRows = (await outbox.ListAsync(second.ProjectId, second.Id)).Entries;
        Assert.Contains(secondRows, row => row.Id == secondProgress.Id
            && row.Kind == SlackOutboxKinds.TerminalResult);
    }

    [Fact]
    public async Task Anchored_agent_reply_promotes_only_the_triggering_turn_progress()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-shared-turns");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var projection = scope.ServiceProvider.GetRequiredService<SlackStatusProjection>();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var firstSource = new SlackMessageIdentity(
            connection.WorkspaceTeamId, "D-shared-turns", "1710000000.000031");
        var secondSource = new SlackMessageIdentity(
            connection.WorkspaceTeamId, "D-shared-turns", "1710000000.000032");
        var firstProgress = await projection.EnqueueWorkingAsync(
            connection.ProjectId, connection.Id, firstSource, null, "first-turn:progress");
        var secondProgress = await projection.EnqueueWorkingAsync(
            connection.ProjectId, connection.Id, secondSource, null, "second-turn:progress");

        var reply = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-shared-turns",
            firstSource.MessageTs,
            "second turn answer",
            connectionId: connection.Id,
            triggeringMessageId: secondSource.MessageTs,
            replyDispatchRef: "second-turn:reply");

        Assert.True(reply.Accepted);
        Assert.Equal(secondProgress.Id, reply.DeliveryId);
        var rows = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries;
        Assert.Contains(rows, row => row.Id == firstProgress.Id
            && row.Kind == SlackOutboxKinds.ReplaceableProgress);
        Assert.Contains(rows, row => row.Id == secondProgress.Id
            && row.Kind == SlackOutboxKinds.TerminalResult);
    }

    [Fact]
    public async Task Anchored_agent_replies_scope_terminal_and_attachment_routing_to_the_requested_connection()
    {
        var first = await CreateConnectionAsync();
        var second = await CreateConnectionAsync(first.ProjectId, "agent-2");
        await CreateThreadMappingAsync(first, "C-shared-terminal", "1710000000.000027");
        await CreateThreadMappingAsync(second, "C-shared-terminal", "1710000000.000027");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();

        var firstReply = await outbox.EnqueueAgentReplyAsync(
            first.ProjectId,
            "C-shared-terminal",
            "1710000000.000027",
            "first answer",
            connectionId: first.Id,
            replyDispatchRef: "shared:turn");
        var secondReply = await outbox.EnqueueAgentReplyAsync(
            second.ProjectId,
            "C-shared-terminal",
            "1710000000.000027",
            "second answer",
            connectionId: second.Id,
            replyDispatchRef: "shared:turn");
        var secondImage = await outbox.EnqueueAgentReplyAsync(
            second.ProjectId,
            "C-shared-terminal",
            "1710000000.000027",
            "second image",
            connectionId: second.Id,
            replyDispatchRef: "shared:image-turn",
            imageUrl: "https://example.com/second.png");

        Assert.Equal(first.Id, firstReply.ConnectionId);
        Assert.Equal(second.Id, secondReply.ConnectionId);
        Assert.NotEqual(firstReply.DeliveryId, secondReply.DeliveryId);
        Assert.Equal(second.Id, secondImage.ConnectionId);
        Assert.All(
            (await outbox.ListAsync(second.ProjectId, second.Id)).Entries,
            row => Assert.Equal(second.Id, row.ConnectionId));
    }

    [Fact]
    public async Task Anchored_agent_reply_rejects_a_connection_without_the_conversation_mapping()
    {
        var mapped = await CreateConnectionAsync();
        var unmapped = await CreateConnectionAsync(mapped.ProjectId, "agent-2");
        await CreateThreadMappingAsync(mapped, "C-mapped-only", "1710000000.000028");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();

        var reply = await outbox.EnqueueAgentReplyAsync(
            unmapped.ProjectId,
            "C-mapped-only",
            "1710000000.000028",
            "wrong owner",
            connectionId: unmapped.Id,
            replyDispatchRef: "unmapped:turn");

        Assert.False(reply.Accepted);
        Assert.Empty((await outbox.ListAsync(unmapped.ProjectId, unmapped.Id)).Entries);
        Assert.Empty((await outbox.ListAsync(mapped.ProjectId, mapped.Id)).Entries);
    }

    [Fact]
    public async Task Agent_reply_route_requires_the_complete_anchor_for_every_dispatch()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-route-anchor");
        var anchor = _replyAnchors["D-route-anchor"];

        using var missingConnection = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new
            {
                workspaceTeamId = anchor.WorkspaceTeamId,
                conversationId = anchor.ConversationId,
                threadTs = anchor.ThreadTs,
                triggeringMessageId = anchor.TriggeringMessageId,
                sessionId = anchor.SessionId,
                dispatchRef = anchor.DispatchRef,
                text = "answer",
            });
        using var missingTrigger = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new
            {
                workspaceTeamId = anchor.WorkspaceTeamId,
                conversationId = anchor.ConversationId,
                threadTs = anchor.ThreadTs,
                connectionId = anchor.ConnectionId,
                sessionId = anchor.SessionId,
                dispatchRef = anchor.DispatchRef,
                text = "answer",
            });
        using var legacy = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new { conversationId = "D-route-anchor", text = "legacy answer" });
        using var anchored = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-route-anchor", "anchored answer"));

        Assert.Equal(HttpStatusCode.BadRequest, missingConnection.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingTrigger.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
        Assert.True(anchored.IsSuccessStatusCode, await anchored.Content.ReadAsStringAsync());
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
        Assert.Equal(progressDispatchRef,
            SlackDeliveryPayload.Parse(promoted.PayloadJson).ProgressDispatchRef);

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

    [Fact]
    public async Task Agent_reply_renders_markdown_bold_code_blocks_lists_and_quotes_to_mrkdwn()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-render-md");

        var body = "**重要** 已完成\n\n- 第一步\n- 第二步\n\n```\ncode block\n```\n\n> 引用";
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-render-md", body));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-render-md");
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.Contains("*重要* 已完成", payload.Text, StringComparison.Ordinal);
        Assert.Contains("• 第一步", payload.Text, StringComparison.Ordinal);
        Assert.Contains("• 第二步", payload.Text, StringComparison.Ordinal);
        Assert.Contains("```\ncode block\n```", payload.Text, StringComparison.Ordinal);
        Assert.Contains("> 引用", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_reply_degrades_tables_and_headings_to_readable_plain_text()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-render-table");

        var body = "# 标题\n\n| A | B |\n|---|---|\n| 1 | 2 |";
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-render-table", body));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-render-table");
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Contains("标题", payload.Text, StringComparison.Ordinal);
        Assert.Contains("A | B", payload.Text, StringComparison.Ordinal);
        Assert.Contains("1 | 2", payload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("---", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_reply_with_public_image_url_posts_an_image_block()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-render-image");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-render-image", "看图", imageUrl: "https://example.com/chart.png"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-render-image");
        Assert.EndsWith(":image", row.DispatchRef, StringComparison.Ordinal);
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.NotNull(payload.Blocks);
        var blocks = JsonNode.Parse(payload.Blocks!.Value.GetRawText())!.AsArray();
        Assert.Equal(2, blocks.Count);
        Assert.Equal("section", blocks[0]!["type"]!.GetValue<string>());
        Assert.Equal("mrkdwn", blocks[0]!["text"]!["type"]!.GetValue<string>());
        Assert.Equal("看图", blocks[0]!["text"]!["text"]!.GetValue<string>());
        Assert.Equal("image", blocks[1]!["type"]!.GetValue<string>());
        Assert.Equal("https://example.com/chart.png", blocks[1]!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_reply_with_image_url_and_no_text_posts_only_the_image()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-render-image-only");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-render-image-only", imageUrl: "https://example.com/p.png"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-render-image-only");
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.True(string.IsNullOrEmpty(payload.Text));
        var blocks = JsonNode.Parse(payload.Blocks!.Value.GetRawText())!.AsArray();
        var imageBlock = Assert.Single(blocks);
        Assert.Equal("image", imageBlock!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_reply_with_local_file_uploads_it_as_a_file_share()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-render-file");

        var fileContentBase64 = Convert.ToBase64String("png-bytes"u8);
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-render-file", fileName: "shot.png", fileContentBase64: fileContentBase64));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await db.SlackOutboxRows.AsNoTracking()
            .Where(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-render-file")
            .ToListAsync();
        var fileRow = Assert.Single(rows);
        Assert.EndsWith(":file", fileRow.DispatchRef, StringComparison.Ordinal);
        var payload = SlackDeliveryPayload.Parse(fileRow.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.UploadFile, payload.Operation);
        Assert.Equal("shot.png", payload.FileName);
        Assert.Equal(fileContentBase64, payload.FileContentBase64);
        Assert.True(string.IsNullOrEmpty(payload.Text));

        // A separate text reply for the same conversation lands under its own
        // dispatch reference and never collides with the file upload row.
        using var textReply = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-render-file", "screenshot attached"));
        Assert.True(textReply.IsSuccessStatusCode, await textReply.Content.ReadAsStringAsync());
        var both = await db.SlackOutboxRows.AsNoTracking()
            .Where(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-render-file")
            .ToListAsync();
        Assert.Single(both, r => r.DispatchRef!.EndsWith(":file", StringComparison.Ordinal));
        Assert.Single(both, r => r.DispatchRef!.EndsWith(":terminal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Agent_reply_with_invalid_base64_file_is_rejected_before_enqueue()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-render-invalid");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-render-invalid", fileName: "x.png", fileContentBase64: "not-base64!!"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackOutboxRows.AsNoTracking()
            .Where(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-render-invalid")
            .ToListAsync());
    }

    private async Task CreateDmMappingAsync(AgentConnection connection, string dmConversationId)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        var sessionId = $"reply-route-session_{Guid.NewGuid():N}";
        var inputId = $"reply-route-input_{Guid.NewGuid():N}";
        var turnId = $"reply-route-turn_{Guid.NewGuid():N}";
        var threadTs = "1710000000.000001";
        var metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = connection.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [GenericAgentSessionMetadata.AgentId] = connection.AgentId,
            [AgentSessionQueryMetadataKeys.ConnectionId] = connection.Id,
            [AgentSessionQueryMetadataKeys.SlackWorkspaceTeamId] = connection.WorkspaceTeamId,
            [AgentSessionQueryMetadataKeys.SlackConversationId] = dmConversationId,
        });
        var provenance = new AgentSessionInputProvenance(
            "slack", connection.WorkspaceTeamId, dmConversationId, null,
            "U_OWNER", threadTs, connection.Id, BoundThreadRootMessageId: null);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.SlackDmSessionMappings.Add(new SlackDmSessionMappingRow
        {
            Id = $"dm_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            SlackUserId = "U_OWNER",
            DmConversationId = dmConversationId,
            CurrentSessionId = sessionId,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).EnsureInitialLaunchAsync(
            new EnsureInitialLaunchCommand(
                inputId, turnId, "seed reply anchor", "agent-connection",
                $"reply-route-job_{Guid.NewGuid():N}", metadata,
                Runtime: "opencode", Provenance: provenance));
        _replyAnchors[dmConversationId] = new ReplyAnchor(
            connection.WorkspaceTeamId, dmConversationId, threadTs, threadTs,
            connection.Id, sessionId, $"slack:{sessionId}:{inputId}");
    }

    private object ReplyBody(
        string conversationId,
        string? text = null,
        string? imageUrl = null,
        string? fileName = null,
        string? fileContentBase64 = null)
    {
        var anchor = _replyAnchors[conversationId];
        return new
        {
            workspaceTeamId = anchor.WorkspaceTeamId,
            conversationId = anchor.ConversationId,
            threadTs = anchor.ThreadTs,
            connectionId = anchor.ConnectionId,
            triggeringMessageId = anchor.TriggeringMessageId,
            sessionId = anchor.SessionId,
            dispatchRef = anchor.DispatchRef,
            text,
            imageUrl,
            fileName,
            fileContentBase64,
        };
    }

    private sealed record ReplyAnchor(
        string WorkspaceTeamId,
        string ConversationId,
        string ThreadTs,
        string TriggeringMessageId,
        string ConnectionId,
        string SessionId,
        string DispatchRef);

    private async Task CreateThreadMappingAsync(
        AgentConnection connection,
        string conversationId,
        string threadTs)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.SlackThreadSessionMappings.Add(new SlackThreadSessionMappingRow
        {
            Id = $"thread_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            ConversationId = conversationId,
            ThreadTs = threadTs,
            SlackUserId = "U_OWNER",
            SessionId = $"session_{Guid.NewGuid():N}",
            RootMessageTs = threadTs,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<AgentConnection> CreateConnectionAsync(
        string? projectId = null,
        string agentId = "agent-1")
    {
        var id = $"connection_{Guid.NewGuid():N}";
        projectId ??= $"project_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        if (!await db.Projects.AnyAsync(project => project.Id == projectId))
        {
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
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
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            WorkspaceTeamId = "T123",
        };
    }

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";
}
