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
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

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
    public async Task Signed_selection_route_launches_an_unbound_agent_under_the_existing_thread_anchor()
    {
        const string owner = "U_SELECTION_THREAD_LAUNCH";
        var promptOwner = await CreateConnectionAsync("thread-launch-owner", "T-selection-thread-launch", owner, "A_THREAD_LAUNCH_OWNER");
        var selected = await CreateConnectionAsync("thread-launch-selected", "T-selection-thread-launch", owner, "A_THREAD_LAUNCH_SELECTED");
        const string conversationId = "C-selection-thread-launch";
        const string threadTs = "1710000000.020110";
        await PostChannelAsync(promptOwner, conversationId, threadTs, null, [promptOwner.BotUserId!], $"<@{promptOwner.BotUserId}> establish the original thread", owner);
        var identity = new SlackMessageIdentity("T-selection-thread-launch", conversationId, "1710000000.020111");
        await PostChannelAsync(
            promptOwner, conversationId, identity.MessageTs, threadTs,
            [promptOwner.BotUserId!, selected.BotUserId!],
            $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> launch selected here", owner);
        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner, identity, selected.ProjectId, selected.Id, "1710000000.020112", threadTs);

        var action = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal("accepted", action.GetProperty("state").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var claim = await scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>()
            .FindAsync(identity.WorkspaceTeamId, identity.ConversationId, identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionDispatchKinds.ThreadLaunch, claim!.DispatchKind);
        Assert.Equal(SlackSelectionStates.Completed, claim.SelectionState);
        var mapping = await scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>()
            .GetSessionIdAsync(selected.ProjectId, identity.WorkspaceTeamId, selected.Id, conversationId, threadTs);
        Assert.Equal(claim.SelectionSessionId, mapping);
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions.Where(row => row.Id == claim.SelectionSessionId).ToListAsync());
    }

    [Fact]
    public async Task Signed_selection_route_continues_an_unmentioned_multi_bound_thread_without_launching()
    {
        const string owner = "U_SELECTION_MULTI_BOUND";
        var connectionA = await CreateConnectionAsync("multi-bound-a", "T-selection-multi-bound", owner, "A_MULTI_BOUND_A");
        var connectionB = await CreateConnectionAsync("multi-bound-b", "T-selection-multi-bound", owner, "A_MULTI_BOUND_B");
        const string conversationId = "C-selection-multi-bound";
        const string threadTs = "1710000000.020120";
        var initialA = await PostChannelAsync(connectionA, conversationId, threadTs, null, [connectionA.BotUserId!], $"<@{connectionA.BotUserId}> start A", owner);
        var initialB = await PostChannelAsync(connectionB, conversationId, threadTs, null, [connectionB.BotUserId!], $"<@{connectionB.BotUserId}> start B", owner);
        var sessionA = initialA.GetProperty("sessionId").GetString()!;
        var sessionB = initialB.GetProperty("sessionId").GetString()!;
        await _fixture.Grains.GetGrain<Mohist.Server.Sessions.Grains.IAgentSessionGrain>(sessionA)
            .AttachPhysicalSessionAsync(new Mohist.Server.Sessions.Grains.AttachPhysicalSessionCommand("runtime-multi-bound-a"));
        await _fixture.Grains.GetGrain<Mohist.Server.Sessions.Grains.IAgentSessionGrain>(sessionB)
            .AttachPhysicalSessionAsync(new Mohist.Server.Sessions.Grains.AttachPhysicalSessionCommand("runtime-multi-bound-b"));

        var identity = new SlackMessageIdentity("T-selection-multi-bound", conversationId, "1710000000.020121");
        var ingress = await PostChannelAsync(connectionA, conversationId, identity.MessageTs, threadTs, [], "continue one bound Agent", owner);
        Assert.Equal("ambiguous", ingress.GetProperty("kind").GetString());
        var choice = await DeliverChooserAndGetChoiceAsync(
            connectionA, identity, connectionB.ProjectId, connectionB.Id, "1710000000.020122", threadTs);

        var action = await PostSelectionAsync(connectionA, choice, owner);
        Assert.Equal("accepted", action.GetProperty("state").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var claim = await scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>()
            .FindAsync(identity.WorkspaceTeamId, identity.ConversationId, identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackAmbiguityKinds.MultiBoundThreadReply, claim!.AmbiguityKind);
        Assert.Equal(SlackSelectionDispatchKinds.ThreadFollowup, claim.DispatchKind);
        Assert.Equal(sessionB, claim.SelectionSessionId);
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Equal(2, await db.AgentSessions.CountAsync(row =>
            row.LabelConnectionId == connectionA.Id || row.LabelConnectionId == connectionB.Id));
    }

    [Fact]
    public async Task Concurrent_different_selection_clicks_commit_one_winner_and_one_execution()
    {
        const string owner = "U_SELECTION_RACE";
        var connectionA = await CreateConnectionAsync("race-a", "T-selection-race", owner, "A_SELECTION_RACE_A");
        var connectionB = await CreateConnectionAsync("race-b", "T-selection-race", owner, "A_SELECTION_RACE_B");
        var identity = new SlackMessageIdentity(
            connectionA.WorkspaceTeamId,
            "C-selection-race",
            "1710000000.020010");
        await PostChannelAsync(
            connectionA,
            identity.ConversationId,
            identity.MessageTs,
            null,
            [connectionA.BotUserId!, connectionB.BotUserId!],
            $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose once",
            owner);

        var choices = await DeliverChooserAndGetChoicesAsync(
            connectionA,
            identity,
            chooserMessageTs: "1710000000.020011");
        Assert.Equal(2, choices.Count);
        var results = await Task.WhenAll(choices.Select(choice =>
            PostSelectionAsync(connectionA, choice, owner)));
        Assert.Contains(results, result => result.GetProperty("state").GetString() == "accepted");
        Assert.All(results, result => Assert.Contains(
            result.GetProperty("state").GetString(),
            new[] { "accepted", "decided" }));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionA.Id
                || row.LabelConnectionId == connectionB.Id)
            .ToListAsync());
        Assert.Single(await db.SlackProviderInboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.SlackMessageIdentity.EndsWith(identity.MessageTs))
            .ToListAsync());
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

    [Fact]
    public async Task Selection_route_rejects_current_policy_lease_deletion_and_binding_drift_before_commit()
    {
        const string owner = "U_SELECTION_MUTABLE";
        var promptOwner = await CreateConnectionAsync("mutable-owner", "T-selection-mutable", owner, "A_SELECTION_MUTABLE_OWNER");
        var selected = await CreateConnectionAsync("mutable-selected", "T-selection-mutable", owner, "A_SELECTION_MUTABLE_SELECTED");

        async Task<(SlackMessageIdentity Identity, SlackSelectionActionPayload Choice)> RenderAsync(
            string suffix,
            string chooserMessageTs)
        {
            var identity = new SlackMessageIdentity(
                promptOwner.WorkspaceTeamId!,
                $"C-selection-mutable-{suffix}",
                $"1710000000.0220{suffix}");
            await PostChannelAsync(
                promptOwner,
                identity.ConversationId,
                identity.MessageTs,
                null,
                [promptOwner.BotUserId!, selected.BotUserId!],
                $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> mutable {suffix}",
                owner);
            var choice = await DeliverChooserAndGetChoiceAsync(
                promptOwner,
                identity,
                selected.ProjectId,
                selected.Id,
                chooserMessageTs);
            return (identity, choice);
        }

        var selectedPolicy = await RenderAsync("01", "1710000000.022101");
        await UpdateConnectionAsync(selected, row => row.OwnerSlackUserId = "U_NEW_SELECTED_OWNER");
        var selectedPolicyResult = await PostSelectionAsync(promptOwner, selectedPolicy.Choice, owner);
        Assert.Equal("unauthorized", selectedPolicyResult.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(selectedPolicy.Identity, selected);
        await UpdateConnectionAsync(selected, row => row.OwnerSlackUserId = owner);

        var promptPolicy = await RenderAsync("02", "1710000000.022102");
        await UpdateConnectionAsync(promptOwner, row => row.OwnerSlackUserId = "U_NEW_PROMPT_OWNER");
        var promptPolicyResult = await PostSelectionAsync(promptOwner, promptPolicy.Choice, owner);
        Assert.Equal("unauthorized", promptPolicyResult.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(promptPolicy.Identity, selected);
        await UpdateConnectionAsync(promptOwner, row => row.OwnerSlackUserId = owner);

        var expiredLease = await RenderAsync("03", "1710000000.022103");
        await ExpireConnectionLeaseAsync(selected);
        var expiredLeaseResult = await PostSelectionAsync(promptOwner, expiredLease.Choice, owner);
        Assert.Equal("unavailable", expiredLeaseResult.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(expiredLease.Identity, selected);
        _connectionLeases[selected.Id] = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(
            _fixture,
            selected.ProjectId,
            selected.Id);

        var bindingDrift = await RenderAsync("04", "1710000000.022104");
        await UpdateConnectionAsync(selected, row => row.BotUserId = "U_DRIFTED_BOT");
        var bindingDriftResult = await PostSelectionAsync(promptOwner, bindingDrift.Choice, owner);
        Assert.Equal("no_longer_valid", bindingDriftResult.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(bindingDrift.Identity, selected);
        await UpdateConnectionAsync(selected, row => row.BotUserId = selected.BotUserId!);

        var deleted = await RenderAsync("05", "1710000000.022105");
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AgentConnectionStore>();
            var deletedConnection = await store.DeleteAsync(selected.ProjectId, selected.Id);
            Assert.NotNull(deletedConnection?.DeletedAt);
            Assert.NotNull((await store.GetAsync(selected.ProjectId, selected.Id))?.DeletedAt);
        }
        var deletedResult = await PostSelectionAsync(promptOwner, deleted.Choice, owner);
        Assert.Equal("unavailable", deletedResult.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(deleted.Identity, selected);
    }

    [Theory]
    [InlineData("prompt", "allowlist_removed")]
    [InlineData("selected", "allowlist_removed")]
    [InlineData("prompt", "member_lost")]
    [InlineData("selected", "member_lost")]
    [InlineData("prompt", "member_unverifiable")]
    [InlineData("selected", "member_unverifiable")]
    [InlineData("prompt", "channel_lost")]
    [InlineData("selected", "channel_lost")]
    [InlineData("prompt", "conversation_unverifiable")]
    [InlineData("selected", "conversation_unverifiable")]
    public async Task Selection_route_rejects_mutable_live_policy_drift_for_both_connection_roles(
        string targetRole,
        string failure)
    {
        const string actor = "U_SELECTION_POLICY_ACTOR";
        var targetPrompt = targetRole == "prompt";
        var promptOwner = await CreateConnectionAsync(
            $"policy-prompt-{targetRole}-{failure}",
            "T-selection-policy-matrix",
            targetPrompt ? "U_OTHER_PROMPT_OWNER" : actor,
            $"A_POLICY_PROMPT_{targetRole}_{failure}");
        var selected = await CreateConnectionAsync(
            $"policy-selected-{targetRole}-{failure}",
            "T-selection-policy-matrix",
            targetPrompt ? actor : "U_OTHER_SELECTED_OWNER",
            $"A_POLICY_SELECTED_{targetRole}_{failure}");
        var target = targetPrompt ? promptOwner : selected;
        var usesAllowlist = failure.StartsWith("allowlist", StringComparison.Ordinal)
            || failure.StartsWith("member", StringComparison.Ordinal);
        await ConfigurePolicyAsync(
            target,
            usesAllowlist ? AccessPolicyKind.Allowlist : AccessPolicyKind.Anyone,
            actor,
            addAllowlistMember: usesAllowlist);
        SlackApi.Clear();
        try
        {
            SlackApi.Responder = request => PolicySlackResponse(
                request,
                actor,
                target.WorkspaceTeamId!,
                memberMode: "regular",
                conversationMode: "member");

            var suffix = $"{targetRole}-{failure}".Replace('_', '-');
            var identity = new SlackMessageIdentity(
                promptOwner.WorkspaceTeamId!,
                $"C-selection-policy-{suffix}",
                $"1710000000.{Math.Abs(suffix.GetHashCode()):D6}");
            await PostChannelAsync(
                promptOwner,
                identity.ConversationId,
                identity.MessageTs,
                null,
                [promptOwner.BotUserId!, selected.BotUserId!],
                $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> mutable policy",
                actor);
            var choice = await DeliverChooserAndGetChoiceAsync(
                promptOwner,
                identity,
                selected.ProjectId,
                selected.Id,
                $"{identity.MessageTs}1");

            if (failure == "allowlist_removed")
            {
                await RemoveAllowedMemberAsync(target, actor);
            }
            else
            {
                var memberMode = failure switch
                {
                    "member_lost" => "deleted",
                    "member_unverifiable" => "unverifiable",
                    _ => "regular",
                };
                var conversationMode = failure switch
                {
                    "channel_lost" => "not_member",
                    "conversation_unverifiable" => "unverifiable",
                    _ => "member",
                };
                SlackApi.Clear();
                SlackApi.Responder = request => PolicySlackResponse(
                    request,
                    actor,
                    target.WorkspaceTeamId!,
                    memberMode,
                    conversationMode);
            }

            var result = await PostSelectionAsync(promptOwner, choice, actor);
            Assert.Equal("unauthorized", result.GetProperty("state").GetString());
            await AssertPendingWithoutOriginalResourcesAsync(identity, selected);
        }
        finally
        {
            SlackApi.Clear();
        }
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

    [Fact]
    public async Task Noninteractive_fallback_and_nonowner_guidance_expire_without_second_message()
    {
        const string owner = "U_SELECTION_FALLBACK";
        var connections = new List<AgentConnection>();
        for (var index = 0; index < 6; index++)
        {
            connections.Add(await CreateConnectionAsync(
                $"fallback-{index}",
                "T-selection-fallback",
                owner,
                $"A_SELECTION_FALLBACK_{index}"));
        }
        var fallbackIdentity = new SlackMessageIdentity(
            "T-selection-fallback",
            "C-selection-fallback",
            "1710000000.020040");
        await PostChannelAsync(
            connections[0],
            fallbackIdentity.ConversationId,
            fallbackIdentity.MessageTs,
            null,
            connections.Select(connection => connection.BotUserId!).ToArray(),
            string.Join(' ', connections.Select(connection => $"<@{connection.BotUserId}>")) + " too many",
            owner);

        var nonOwnerA = await CreateConnectionAsync("nonowner-a", "T-selection-nonowner", "U_OWNER_A", "A_SELECTION_NONOWNER_A");
        var nonOwnerB = await CreateConnectionAsync("nonowner-b", "T-selection-nonowner", "U_OWNER_B", "A_SELECTION_NONOWNER_B");
        var nonOwnerIdentity = new SlackMessageIdentity(
            "T-selection-nonowner",
            "C-selection-nonowner",
            "1710000000.020041");
        await PostChannelAsync(
            nonOwnerA,
            nonOwnerIdentity.ConversationId,
            nonOwnerIdentity.MessageTs,
            null,
            [nonOwnerA.BotUserId!, nonOwnerB.BotUserId!],
            $"<@{nonOwnerA.BotUserId}> <@{nonOwnerB.BotUserId}> unauthorized",
            "U_INTRUDER");

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        await NewSelectionWorker().ProcessPendingAsync();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        foreach (var identity in new[] { fallbackIdentity, nonOwnerIdentity })
        {
            var rows = await db.SlackOutboxRows
                .Where(row => row.WorkspaceTeamId == identity.WorkspaceTeamId
                    && row.ConversationId == identity.ConversationId)
                .ToListAsync();
            Assert.Single(rows);
            Assert.DoesNotContain(rows, row => row.DispatchRef ==
                SlackAmbiguousPromptStore.SettlementDispatchRef(
                    identity.WorkspaceTeamId,
                    identity.ConversationId,
                    identity.MessageTs));
        }
        await RefreshAllConnectionLeasesAsync();
    }

    private async Task RefreshAllConnectionLeasesAsync()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var leases = scope.ServiceProvider.GetRequiredService<SlackAdapterLeaseService>();
        var targets = await db.AgentConnections.AsNoTracking()
            .Where(row => row.DeletedAt == null && row.ProviderKind == ConnectionProviderKind.Slack)
            .Select(row => new { row.ProjectId, row.Id })
            .ToListAsync();
        foreach (var target in targets)
        {
            var targetRef = new SlackLeaseTargetRef.Connection(target.ProjectId, target.Id);
            var active = await scope.ServiceProvider.GetRequiredService<ISlackLeaseStore>()
                .GetActiveAsync(targetRef.TargetKey);
            if (active is not null && active.ExpiresAt > _fixture.TimeProvider.GetUtcNow())
            {
                await leases.RenewLeaseAsync(
                    "spec-operator",
                    targetRef,
                    active.LeaseId,
                    active.AdapterId,
                    CancellationToken.None);
                _connectionLeases[target.Id] = active.LeaseId;
            }
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

    private async Task ConfigurePolicyAsync(
        AgentConnection connection,
        string policy,
        string actor,
        bool addAllowlistMember)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.AgentConnections.SingleAsync(candidate =>
            candidate.ProjectId == connection.ProjectId && candidate.Id == connection.Id);
        row.AccessPolicy = policy;
        row.UpdatedAt = _fixture.TimeProvider.GetUtcNow();
        if (addAllowlistMember)
        {
            db.SlackConnectionAllowedMembers.Add(new SlackConnectionAllowedMemberRow
            {
                Id = $"slkalm_{Guid.NewGuid():N}",
                ProjectId = connection.ProjectId,
                ConnectionId = connection.Id,
                SlackUserId = actor,
                WorkspaceTeamId = connection.WorkspaceTeamId!,
                CreatedAt = _fixture.TimeProvider.GetUtcNow(),
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task RemoveAllowedMemberAsync(AgentConnection connection, string actor)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await db.SlackConnectionAllowedMembers
            .Where(row => row.ProjectId == connection.ProjectId
                && row.ConnectionId == connection.Id
                && row.SlackUserId == actor)
            .ExecuteDeleteAsync();
    }

    private static HttpResponseMessage PolicySlackResponse(
        HttpRequestMessage request,
        string actor,
        string teamId,
        string memberMode,
        string conversationMode)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.EndsWith("users.info", StringComparison.Ordinal))
        {
            return memberMode switch
            {
                "unverifiable" => SlackApiTestScript.JsonResponse("""{"ok":false,"error":"internal_error"}"""),
                "deleted" => SlackApiTestScript.JsonResponse(JsonSerializer.Serialize(new
                {
                    ok = true,
                    user = new { id = actor, team_id = teamId, deleted = true, is_bot = false, is_app_user = false, is_restricted = false, is_ultra_restricted = false, is_stranger = false },
                })),
                _ => SlackApiTestScript.JsonResponse(JsonSerializer.Serialize(new
                {
                    ok = true,
                    user = new { id = actor, team_id = teamId, deleted = false, is_bot = false, is_app_user = false, is_restricted = false, is_ultra_restricted = false, is_stranger = false },
                })),
            };
        }
        if (path.EndsWith("conversations.info", StringComparison.Ordinal))
        {
            return conversationMode switch
            {
                "unverifiable" => SlackApiTestScript.JsonResponse("""{"ok":false,"error":"internal_error"}"""),
                "not_member" => SlackApiTestScript.JsonResponse("""{"ok":true,"channel":{"id":"C","is_member":false}}"""),
                _ => SlackApiTestScript.JsonResponse("""{"ok":true,"channel":{"id":"C","is_member":true}}"""),
            };
        }
        return SlackApiTestScript.JsonResponse("""{"ok":false,"error":"unexpected_slack_api_call"}""");
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

    private SlackAgentSelectionObligationWorker NewSelectionWorker() => new(
        _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
        _fixture.TimeProvider,
        Options.Create(new SlackProviderOptions()),
        NullLogger<SlackAgentSelectionObligationWorker>.Instance);

}
