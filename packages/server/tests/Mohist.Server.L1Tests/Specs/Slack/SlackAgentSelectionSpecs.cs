using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Slack;

public sealed partial class SlackMultiAgentIngressSpecs
{
    [Fact]
    public async Task Signed_selection_route_launches_only_the_chosen_cross_project_agent()
    {
        const string owner = "U_SELECTION_OWNER";
        var promptOwner = await CreateConnectionAsync("route-owner", "T-selection-route", owner, "A_SELECTION_OWNER");
        var selected = await CreateConnectionAsync("route-selected", "T-selection-route", owner, "A_SELECTION_SELECTED");
        var identity = new SlackMessageIdentity(
            promptOwner.WorkspaceTeamId,
            "C-selection-route",
            "1710000000.020000");

        var ingress = await PostChannelAsync(
            promptOwner,
            identity.ConversationId,
            identity.MessageTs,
            threadTs: null,
            mentions: [promptOwner.BotUserId!, selected.BotUserId!],
            text: $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> route the original task",
            senderSlackUserId: owner);
        Assert.Equal("ambiguous", ingress.GetProperty("kind").GetString());

        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner,
            identity,
            selected.ProjectId,
            selected.Id,
            chooserMessageTs: "1710000000.020001");
        var action = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal("accepted", action.GetProperty("state").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await prompts.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Completed, claim!.SelectionState);
        Assert.Equal(selected.ProjectId, claim.ChosenProjectId);
        Assert.Equal(selected.Id, claim.ChosenConnectionId);

        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.LabelConnectionId == selected.Id)
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == promptOwner.Id)
            .ToListAsync());
        Assert.Single(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == selected.Id
                && row.SlackMessageIdentity.EndsWith(identity.MessageTs))
            .ToListAsync());
    }

    [Fact]
    public async Task Ambiguous_prompt_redelivery_reuses_durable_candidate_snapshot_after_candidate_drift()
    {
        const string owner = "U_SELECTION_RETRY";
        var promptOwner = await CreateConnectionAsync("retry-owner", "T-selection-retry", owner, "A_SELECTION_RETRY_OWNER");
        var selected = await CreateConnectionAsync("retry-selected", "T-selection-retry", owner, "A_SELECTION_RETRY_SELECTED");
        var identity = new SlackMessageIdentity(
            promptOwner.WorkspaceTeamId!,
            "C-selection-retry",
            "1710000000.020050");

        var first = await PostChannelAsync(
            promptOwner,
            identity.ConversationId,
            identity.MessageTs,
            threadTs: null,
            mentions: [promptOwner.BotUserId!, selected.BotUserId!],
            text: $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> retry the chooser",
            senderSlackUserId: owner);
        Assert.Equal("ambiguous", first.GetProperty("kind").GetString());

        var drifted = await CreateConnectionAsync("retry-drifted", "T-selection-retry", owner, "A_SELECTION_RETRY_DRIFTED");
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var selectedRow = await db.AgentConnections.SingleAsync(row =>
                row.ProjectId == selected.ProjectId && row.Id == selected.Id);
            var driftedRow = await db.AgentConnections.SingleAsync(row =>
                row.ProjectId == drifted.ProjectId && row.Id == drifted.Id);
            driftedRow.BotUserId = selected.BotUserId!;
            selectedRow.BotUserId = "U_SELECTION_DRIFTED";
            await db.SlackOutboxRows
                .Where(row => row.ProjectId == promptOwner.ProjectId
                    && row.ConnectionId == promptOwner.Id
                    && row.Kind == SlackOutboxKinds.UserAction
                    && row.DispatchRef == SlackAmbiguousPromptStore.PromptDispatchRef(
                        identity.WorkspaceTeamId,
                        identity.ConversationId,
                        identity.MessageTs))
                .ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }

        var retry = await PostChannelAsync(
            promptOwner,
            identity.ConversationId,
            identity.MessageTs,
            threadTs: null,
            mentions: [promptOwner.BotUserId!, selected.BotUserId!],
            text: $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> retry the chooser",
            senderSlackUserId: owner);
        Assert.Equal("ambiguous", retry.GetProperty("kind").GetString());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var prompts = verify.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await prompts.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        var durableCandidates = JSON.Deserialize<List<SlackSelectionCandidateReference>>(
            claim!.CandidateReferencesJson)!;
        var outbox = verify.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var chooser = await outbox.FindByDispatchRefAsync(
            promptOwner.ProjectId,
            promptOwner.Id,
            SlackOutboxKinds.UserAction,
            SlackAmbiguousPromptStore.PromptDispatchRef(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs));
        Assert.NotNull(chooser);
        var blocks = SlackDeliveryPayload.Parse(chooser!.PayloadJson).Blocks;
        Assert.NotNull(blocks);
        var rendered = JSON.Deserialize<SlackSelectionActionPayload>(
            blocks!.Value[0].GetProperty("elements")[0].GetProperty("value").GetString()!);
        Assert.NotNull(rendered);
        Assert.Equal(durableCandidates, rendered!.CandidateReferences);
        Assert.DoesNotContain(rendered.CandidateReferences,
            candidate => candidate.ConnectionId == drifted.Id);
        Assert.Contains(rendered.CandidateReferences,
            candidate => candidate.ConnectionId == selected.Id
                && candidate.BotUserId == selected.BotUserId);
    }

    [Fact]
    public async Task Signed_selection_route_follows_up_the_already_bound_thread_with_retained_files()
    {
        const string owner = "U_SELECTION_BOUND";
        var promptOwner = await CreateConnectionAsync("bound-route-owner", "T-selection-bound", owner, "A_SELECTION_BOUND_OWNER");
        var selected = await CreateConnectionAsync("bound-route-selected", "T-selection-bound", owner, "A_SELECTION_BOUND_SELECTED");
        const string conversationId = "C-selection-bound";
        const string threadTs = "1710000000.020100";
        var initial = await PostChannelAsync(selected, conversationId, threadTs, null, [selected.BotUserId!], $"<@{selected.BotUserId}> start selected thread", owner);
        var sessionId = initial.GetProperty("sessionId").GetString()!;
        await _fixture.Grains.GetGrain<Mohist.Server.Sessions.Grains.IAgentSessionGrain>(sessionId)
            .AttachPhysicalSessionAsync(new Mohist.Server.Sessions.Grains.AttachPhysicalSessionCommand("runtime-selection-bound"));

        var identity = new SlackMessageIdentity("T-selection-bound", conversationId, "1710000000.020101");
        await PostChannelAsync(
            promptOwner, conversationId, identity.MessageTs, threadTs,
            [promptOwner.BotUserId!, selected.BotUserId!],
            $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> continue with retained file",
            owner,
            [new SlackIngressFile("F-selection-bound", "context.txt", "text/plain", 12)]);
        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner, identity, selected.ProjectId, selected.Id, "1710000000.020102", threadTs);

        var action = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal("accepted", action.GetProperty("state").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var claim = await scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>()
            .FindAsync(identity.WorkspaceTeamId, identity.ConversationId, identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Completed, claim!.SelectionState);
        Assert.Equal(SlackSelectionDispatchKinds.ThreadFollowup, claim.DispatchKind);
        Assert.Equal(sessionId, claim.SelectionSessionId);
        Assert.Contains("F-selection-bound", claim.FilesJson, StringComparison.Ordinal);
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions.Where(row => row.LabelConnectionId == selected.Id).ToListAsync());
        var session = JSON.Deserialize<AgentSession>((await db.AgentSessions.SingleAsync(row => row.Id == sessionId)).State)!;
        Assert.Single(session.Status.Inputs!, input =>
            input.Id == claim.SelectionInputId
            && input.Text.Contains("continue with retained file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selection_route_rejects_tampered_wrong_actor_and_copied_chooser_actions_without_resources()
    {
        const string owner = "U_SELECTION_REJECTIONS";
        var promptOwner = await CreateConnectionAsync("reject-owner", "T-selection-rejections", owner, "A_SELECTION_REJECT_OWNER");
        var selected = await CreateConnectionAsync("reject-selected", "T-selection-rejections", owner, "A_SELECTION_REJECT_SELECTED");

        async Task<(SlackMessageIdentity Identity, SlackSelectionActionPayload Choice)> RenderAsync(
            string suffix,
            string chooserMessageTs)
        {
            var identity = new SlackMessageIdentity(
                promptOwner.WorkspaceTeamId!,
                $"C-selection-rejections-{suffix}",
                $"1710000000.0210{suffix}");
            await PostChannelAsync(
                promptOwner,
                identity.ConversationId,
                identity.MessageTs,
                null,
                [promptOwner.BotUserId!, selected.BotUserId!],
                $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> reject {suffix}",
                owner);
            var choice = await DeliverChooserAndGetChoiceAsync(
                promptOwner,
                identity,
                selected.ProjectId,
                selected.Id,
                chooserMessageTs);
            return (identity, choice);
        }

        var tampered = await RenderAsync("01", "1710000000.021101");
        var tamperedResult = await PostSelectionAsync(
            promptOwner,
            tampered.Choice with { Signature = new string('0', 64) },
            owner);
        Assert.Equal("invalid_action", tamperedResult.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(tampered.Identity, selected);

        var wrongActor = await RenderAsync("02", "1710000000.021102");
        var wrongActorResult = await PostSelectionAsync(promptOwner, wrongActor.Choice, "U_SOMEONE_ELSE");
        Assert.Equal("unauthorized", wrongActorResult.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(wrongActor.Identity, selected);

        var copied = await RenderAsync("03", "1710000000.021103");
        var copiedResult = await PostSelectionAsync(
            promptOwner,
            copied.Choice,
            owner,
            chooserMessageTsOverride: "1710000000.999999");
        Assert.Equal("stale_action", copiedResult.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(copied.Identity, selected);
    }



    [Theory]
    [InlineData("selected_owner", "unauthorized")]
    [InlineData("prompt_owner", "unauthorized")]
    [InlineData("expired_lease", "unavailable")]
    [InlineData("binding_drift", "no_longer_valid")]
    [InlineData("deleted", "unavailable")]
    public async Task Selection_route_rejects_mutable_candidate_state_before_commit(
        string scenario,
        string expectedState)
    {
        const string owner = "U_SELECTION_MUTABLE";
        var promptOwner = await CreateConnectionAsync(
            $"mutable-owner-{scenario}",
            "T-selection-mutable",
            owner,
            $"A_SELECTION_MUTABLE_OWNER_{scenario}");
        var selected = await CreateConnectionAsync(
            $"mutable-selected-{scenario}",
            "T-selection-mutable",
            owner,
            $"A_SELECTION_MUTABLE_SELECTED_{scenario}");
        var identity = new SlackMessageIdentity(
            promptOwner.WorkspaceTeamId!,
            $"C-selection-mutable-{scenario}",
            $"1710000000.{scenario.GetHashCode() & int.MaxValue:D6}");
        await PostChannelAsync(
            promptOwner,
            identity.ConversationId,
            identity.MessageTs,
            null,
            [promptOwner.BotUserId!, selected.BotUserId!],
            $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> mutable {scenario}",
            owner);
        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner,
            identity,
            selected.ProjectId,
            selected.Id,
            $"{identity.MessageTs}1");

        switch (scenario)
        {
            case "selected_owner":
                await UpdateConnectionAsync(selected, row => row.OwnerSlackUserId = "U_NEW_SELECTED_OWNER");
                break;
            case "prompt_owner":
                await UpdateConnectionAsync(promptOwner, row => row.OwnerSlackUserId = "U_NEW_PROMPT_OWNER");
                break;
            case "expired_lease":
                await ExpireConnectionLeaseAsync(selected);
                break;
            case "binding_drift":
                await UpdateConnectionAsync(selected, row => row.BotUserId = "U_DRIFTED_BOT");
                break;
            case "deleted":
                await using (var scope = _fixture.Services.CreateAsyncScope())
                {
                    var store = scope.ServiceProvider.GetRequiredService<AgentConnectionStore>();
                    var deletedConnection = await store.DeleteAsync(selected.ProjectId, selected.Id);
                    Assert.NotNull(deletedConnection?.DeletedAt);
                    Assert.NotNull((await store.GetAsync(selected.ProjectId, selected.Id))?.DeletedAt);
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown mutable selection scenario '{scenario}'.");
        }

        var result = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal(expectedState, result.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(identity, selected);
    }



    [Fact]
    public async Task Followup_selection_revalidates_executability_before_committing()
    {
        const string owner = "U_SELECTION_EXEC";
        var promptOwner = await CreateConnectionAsync("exec-owner", "T-selection-exec", owner, "A_SELECTION_EXEC_OWNER");
        var selected = await CreateConnectionAsync("exec-selected", "T-selection-exec", owner, "A_SELECTION_EXEC_SELECTED");
        const string conversationId = "C-selection-exec";
        const string threadTs = "1710000000.020020";
        const string messageTs = "1710000000.020021";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>()
                .UpsertAsync(
                    selected.ProjectId,
                    selected.WorkspaceTeamId!,
                    selected.Id,
                    conversationId,
                    threadTs,
                    owner,
                    "missing-session-before-admission",
                    threadTs);
        }
        await PostChannelAsync(
            promptOwner,
            conversationId,
            messageTs,
            threadTs,
            [promptOwner.BotUserId!, selected.BotUserId!],
            $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> continue only if executable",
            owner);
        var identity = new SlackMessageIdentity(promptOwner.WorkspaceTeamId!, conversationId, messageTs);
        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner,
            identity,
            selected.ProjectId,
            selected.Id,
            chooserMessageTs: "1710000000.020022",
            threadTs);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var row = await db.Agents.SingleAsync(agent =>
                agent.ProjectId == selected.ProjectId && agent.Id == selected.AgentId);
            var agent = AgentStore.Deserialize(row.State)!;
            agent.AgentConfig = null;
            row.State = AgentStore.Serialize(agent);
            await db.SaveChangesAsync();
        }

        var result = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal("agent_not_configured", result.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(identity, selected);
    }



    [Fact]
    public async Task Dangling_followup_mapping_is_rejected_before_selection_commit()
    {
        const string owner = "U_SELECTION_STALE";
        var promptOwner = await CreateConnectionAsync("stale-owner", "T-selection-stale", owner, "A_SELECTION_STALE_OWNER");
        var selected = await CreateConnectionAsync("stale-selected", "T-selection-stale", owner, "A_SELECTION_STALE_SELECTED");
        const string conversationId = "C-selection-stale";
        const string threadTs = "1710000000.020030";
        const string messageTs = "1710000000.020031";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>()
                .UpsertAsync(
                    selected.ProjectId,
                    selected.WorkspaceTeamId!,
                    selected.Id,
                    conversationId,
                    threadTs,
                    owner,
                    "missing-selected-session",
                    threadTs);
        }
        await PostChannelAsync(
            promptOwner,
            conversationId,
            messageTs,
            threadTs,
            [promptOwner.BotUserId!, selected.BotUserId!],
            $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> stale target",
            owner);
        var identity = new SlackMessageIdentity(promptOwner.WorkspaceTeamId!, conversationId, messageTs);
        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner,
            identity,
            selected.ProjectId,
            selected.Id,
            chooserMessageTs: "1710000000.020032",
            threadTs);

        var result = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal("no_longer_valid", result.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(identity, selected);
    }



    private async Task UpdateConnectionAsync(
        AgentConnection connection,
        Action<AgentConnectionRow> update)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.AgentConnections.SingleAsync(candidate =>
            candidate.ProjectId == connection.ProjectId && candidate.Id == connection.Id);
        update(row);
        row.UpdatedAt = _fixture.TimeProvider.GetUtcNow();
        await db.SaveChangesAsync();
    }



    private async Task ExpireConnectionLeaseAsync(AgentConnection connection)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var targetKey = new SlackLeaseTargetRef.Connection(connection.ProjectId, connection.Id).TargetKey;
        var row = await db.SlackAdapterLeases.SingleAsync(lease => lease.TargetKey == targetKey);
        row.ExpiresAt = _fixture.TimeProvider.GetUtcNow() - TimeSpan.FromSeconds(1);
        await db.SaveChangesAsync();
    }



    private async Task AssertPendingWithoutOriginalResourcesAsync(
        SlackMessageIdentity identity,
        AgentConnection selected)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await prompts.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Pending, claim!.SelectionState);
        Assert.Null(claim.ChosenConnectionId);

        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == selected.Id
                && row.SlackMessageIdentity.EndsWith(identity.MessageTs))
            .ToListAsync());
    }



    private async Task RefreshAllConnectionLeasesAsync()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var now = _fixture.TimeProvider.GetUtcNow();
        var renewedUntil = now + SlackAdapterLeaseService.RuntimeLeaseTtl;
        var activeLeases = await db.SlackAdapterLeases
            .Where(row => row.LeaseKind == SlackLeaseKind.Runtime
                && row.LeaseId != null
                && row.AdapterId != null)
            .ToListAsync();
        foreach (var active in activeLeases)
        {
            active.ExpiresAt = renewedUntil;
            active.UpdatedAt = now;
        }
        await db.SaveChangesAsync();

        foreach (var connection in await db.AgentConnections.AsNoTracking()
            .Where(row => row.DeletedAt == null && row.ProviderKind == ConnectionProviderKind.Slack)
            .Select(row => new { row.ProjectId, row.Id })
            .ToListAsync())
        {
            var targetKey = new SlackLeaseTargetRef.Connection(connection.ProjectId, connection.Id).TargetKey;
            var active = activeLeases.SingleOrDefault(lease => lease.TargetKey == targetKey);
            if (active is not null)
                _connectionLeases[connection.Id] = active.LeaseId!;
        }
    }

    private async Task<IReadOnlyList<SlackSelectionActionPayload>> DeliverChooserAndGetChoicesAsync(
        AgentConnection promptOwner,
        SlackMessageIdentity identity,
        string chooserMessageTs)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var chooser = await outbox.FindByDispatchRefAsync(
            promptOwner.ProjectId,
            promptOwner.Id,
            SlackOutboxKinds.UserAction,
            SlackAmbiguousPromptStore.PromptDispatchRef(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs));
        Assert.NotNull(chooser);
        await outbox.MarkDeliveredAsync(
            promptOwner.ProjectId,
            chooser!.Id,
            new SlackProviderMessageIdentity(identity.ConversationId, chooserMessageTs));

        var blocks = SlackDeliveryPayload.Parse(chooser.PayloadJson).Blocks;
        Assert.NotNull(blocks);
        return blocks!.Value.EnumerateArray()
            .SelectMany(block => block.GetProperty("elements").EnumerateArray())
            .Select(button => JSON.Deserialize<SlackSelectionActionPayload>(
                button.GetProperty("value").GetString()!)!)
            .ToArray();
    }

    private async Task<SlackSelectionActionPayload> DeliverChooserAndGetChoiceAsync(
        AgentConnection promptOwner,
        SlackMessageIdentity identity,
        string selectedProjectId,
        string selectedConnectionId,
        string chooserMessageTs,
        string? threadTs = null)
    {
        var choices = await DeliverChooserAndGetChoicesAsync(
            promptOwner,
            identity,
            chooserMessageTs);
        var choice = Assert.Single(choices, candidate =>
            string.Equals(candidate.ChosenProjectId, selectedProjectId, StringComparison.Ordinal)
            && string.Equals(candidate.ChosenConnectionId, selectedConnectionId, StringComparison.Ordinal));
        Assert.Equal(threadTs, choice.ThreadTs);
        return choice;
    }

    private async Task<JsonElement> PostSelectionAsync(
        AgentConnection promptOwner,
        SlackSelectionActionPayload choice,
        string actorSlackUserId,
        string? chooserMessageTsOverride = null)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{promptOwner.ProjectId}/slack-connections/{promptOwner.Id}/interactions",
            new
            {
                apiAppId = promptOwner.AppId,
                eventType = "block_actions",
                interactionId = $"selection-{Guid.NewGuid():N}",
                teamId = choice.WorkspaceTeamId,
                conversationId = choice.ConversationId,
                messageTs = chooserMessageTsOverride ?? await ChooserMessageTsAsync(promptOwner, choice),
                threadTs = choice.ThreadTs,
                actorSlackUserId,
                actionId = SlackSelectionActionPayload.ActionId,
                actionValue = JSON.Serialize(choice),
                leaseId = _connectionLeases[promptOwner.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<string> ChooserMessageTsAsync(
        AgentConnection promptOwner,
        SlackSelectionActionPayload choice)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var chooser = await outbox.FindByDispatchRefAsync(
            promptOwner.ProjectId,
            promptOwner.Id,
            SlackOutboxKinds.UserAction,
            SlackAmbiguousPromptStore.PromptDispatchRef(
                choice.WorkspaceTeamId,
                choice.ConversationId,
                choice.OriginalMessageTs));
        Assert.NotNull(chooser);
        var providerIdentity = SlackDeliveryPayload.Parse(chooser!.PayloadJson).ProviderMessageIdentity;
        Assert.NotNull(providerIdentity);
        return providerIdentity!.Value.MessageTs;
    }


}
