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

[Collection("SharedSlackApi")]
public sealed partial class SlackMultiAgentIngressSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public SlackMultiAgentIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    private SlackApiTestScript SlackApi =>
        _fixture.Services.GetRequiredService<SlackApiTestScript>();

    public ValueTask InitializeAsync()
    {
        SlackApi.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SlackApi.Clear();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Multi_bot_mention_starts_no_work_and_prompts_once()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi", "U_BOT_A", "A_BOT_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi", "U_BOT_B", "A_BOT_B");

        var body = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-bot",
            messageTs = "1710000000.010100",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionA.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> who answers?",
            leaseId = _connectionLeases[connectionA.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ambiguous", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionA.Id || row.LabelConnectionId == connectionB.Id)
            .ToListAsync());
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-bot")
            .ToListAsync());
        Assert.Empty(await db.SlackThreadSessionMappings
            .Where(row => row.ConversationId == "C-multi-bot")
            .ToListAsync());
    }

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

    [Fact]
    public async Task Recovery_retries_transient_dispatch_failures_beyond_three_attempts_without_settling()
    {
        var promptOwner = await CreateConnectionAsync("retry-owner", "T-selection-retry", "U_RETRY", "A_SELECTION_RETRY_OWNER");
        var selected = await CreateConnectionAsync("retry-selected", "T-selection-retry", "U_RETRY", "A_SELECTION_RETRY_SELECTED");
        var identity = new SlackMessageIdentity("T-selection-retry", "C-selection-retry", "1710000000.020050");
        var candidates = new[]
        {
            new SlackSelectionCandidateReference(promptOwner.ProjectId, promptOwner.Id, promptOwner.BotUserId),
            new SlackSelectionCandidateReference(selected.ProjectId, selected.Id, selected.BotUserId),
        };

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            await prompts.TryClaimAsync(
                promptOwner.ProjectId,
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                null,
                promptOwner.Id,
                candidates,
                "U_RETRY",
                "retry transient failure",
                "not-json",
                SlackAmbiguityKinds.RootMultiMention);
            var ids = SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity);
            await prompts.TryDecideAsync(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                selected.ProjectId,
                selected.Id,
                SlackSelectionDispatchKinds.RootLaunch,
                ids.SessionId,
                ids.InputId,
                ids.TurnId);
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
            await NewSelectionWorker().ProcessPendingAsync();
        }

        await using var verify = _fixture.Services.CreateAsyncScope();
        var promptsAfter = verify.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await promptsAfter.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Decided, claim!.SelectionState);
        Assert.Equal(4, claim.AttemptCount);
        Assert.Null(claim.SettleReason);

        var outbox = verify.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        Assert.Null(await outbox.FindByDispatchRefAsync(
            promptOwner.ProjectId,
            promptOwner.Id,
            SlackOutboxKinds.UserAction,
            SlackAmbiguousPromptStore.SettlementDispatchRef(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs)));
        await RefreshAllConnectionLeasesAsync();
    }

    [Fact]
    public async Task Recovery_routes_a_committed_thread_launch_bound_race_as_the_original_followup()
    {
        const string owner = "U_BOUND_RACE";
        var promptOwner = await CreateConnectionAsync("bound-owner", "T-bound-race", owner, "A_BOUND_RACE_OWNER");
        var selected = await CreateConnectionAsync("bound-selected", "T-bound-race", owner, "A_BOUND_RACE_SELECTED");
        const string conversationId = "C-bound-race";
        const string threadTs = "1710000000.020060";
        var initial = await PostChannelAsync(
            selected,
            conversationId,
            threadTs,
            null,
            [selected.BotUserId!],
            $"<@{selected.BotUserId}> establish selected Session",
            owner);
        var boundSessionId = initial.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(boundSessionId));
        await _fixture.Grains.GetGrain<Mohist.Server.Sessions.Grains.IAgentSessionGrain>(boundSessionId!)
            .AttachPhysicalSessionAsync(new Mohist.Server.Sessions.Grains.AttachPhysicalSessionCommand(
                "runtime-bound-race"));

        var identity = new SlackMessageIdentity("T-bound-race", conversationId, "1710000000.020061");
        var candidates = new[]
        {
            new SlackSelectionCandidateReference(promptOwner.ProjectId, promptOwner.Id, promptOwner.BotUserId),
            new SlackSelectionCandidateReference(selected.ProjectId, selected.Id, selected.BotUserId),
        };
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            await prompts.TryClaimAsync(
                promptOwner.ProjectId,
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                threadTs,
                promptOwner.Id,
                candidates,
                owner,
                "retain this raced message",
                "[]",
                SlackAmbiguityKinds.ThreadMultiMention);
            var ids = SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity);
            await prompts.TryDecideAsync(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                selected.ProjectId,
                selected.Id,
                SlackSelectionDispatchKinds.ThreadLaunch,
                ids.SessionId,
                ids.InputId,
                ids.TurnId);
        }

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await NewSelectionWorker().ProcessPendingAsync();
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await NewSelectionWorker().ProcessPendingAsync();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var claim = await verify.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>()
            .FindAsync(identity.WorkspaceTeamId, identity.ConversationId, identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Completed, claim!.SelectionState);
        Assert.Equal(SlackSelectionDispatchKinds.ThreadFollowup, claim.DispatchKind);
        Assert.Equal(boundSessionId, claim.SelectionSessionId);

        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.LabelConnectionId == selected.Id)
            .ToListAsync());
        var inbox = Assert.Single(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == selected.Id
                && row.SlackMessageIdentity.EndsWith(identity.MessageTs))
            .ToListAsync());
        Assert.Equal(SlackProviderInboxRouteKinds.FollowupThread, inbox.RouteKind);
        Assert.Equal(boundSessionId, inbox.RouteSessionId);
        var sessionRow = await db.AgentSessions.SingleAsync(row => row.Id == boundSessionId);
        var session = JSON.Deserialize<AgentSession>(sessionRow.State)!;
        Assert.Single(session.Status.Inputs!, input =>
            input.Id == claim.SelectionInputId
            && input.Text == "retain this raced message");
        Assert.Single(session.Status.Turns!, turn => turn.Id == claim.SelectionTurnId);
        await RefreshAllConnectionLeasesAsync();
    }

    [Fact]
    public async Task Recovery_replays_followup_accept_after_crash_between_inbox_and_session_acceptance()
    {
        const string owner = "U_BOUND_CRASH";
        var promptOwner = await CreateConnectionAsync("crash-owner", "T-bound-crash", owner, "A_BOUND_CRASH_OWNER");
        var selected = await CreateConnectionAsync("crash-selected", "T-bound-crash", owner, "A_BOUND_CRASH_SELECTED");
        const string conversationId = "C-bound-crash";
        const string threadTs = "1710000000.020070";
        var initial = await PostChannelAsync(
            selected,
            conversationId,
            threadTs,
            null,
            [selected.BotUserId!],
            $"<@{selected.BotUserId}> establish crash Session",
            owner);
        var boundSessionId = initial.GetProperty("sessionId").GetString()!;
        await _fixture.Grains.GetGrain<Mohist.Server.Sessions.Grains.IAgentSessionGrain>(boundSessionId)
            .AttachPhysicalSessionAsync(new Mohist.Server.Sessions.Grains.AttachPhysicalSessionCommand(
                "runner-bound-crash",
                "runtime-bound-crash"));
        var identity = new SlackMessageIdentity("T-bound-crash", conversationId, "1710000000.020071");
        var candidates = new[]
        {
            new SlackSelectionCandidateReference(promptOwner.ProjectId, promptOwner.Id, promptOwner.BotUserId),
            new SlackSelectionCandidateReference(selected.ProjectId, selected.Id, selected.BotUserId),
        };

        SlackAmbiguousPromptSnapshot decided;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            await prompts.TryClaimAsync(
                promptOwner.ProjectId,
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                threadTs,
                promptOwner.Id,
                candidates,
                owner,
                "survive the inbox crash window",
                "[]",
                SlackAmbiguityKinds.ThreadMultiMention);
            var launchIds = SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity);
            await prompts.TryDecideAsync(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                selected.ProjectId,
                selected.Id,
                SlackSelectionDispatchKinds.ThreadLaunch,
                launchIds.SessionId,
                launchIds.InputId,
                launchIds.TurnId);
            decided = await prompts.ResolveBoundThreadLaunchAsync(
                (await prompts.FindAsync(identity.WorkspaceTeamId, identity.ConversationId, identity.MessageTs))!.Id,
                boundSessionId);
            var inbox = scope.ServiceProvider.GetRequiredService<SlackProviderInboxStore>();
            await inbox.AcceptAsync(
                new SlackProviderInboxDraft(selected.ProjectId, selected.Id, identity, owner, threadTs),
                new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.FollowupThread, boundSessionId));
        }

        Assert.Equal(SlackSelectionDispatchKinds.ThreadFollowup, decided.DispatchKind);
        Assert.Equal(boundSessionId, decided.SelectionSessionId);
        await using (var recoveryScope = _fixture.Services.CreateAsyncScope())
        {
            var recovery = await recoveryScope.ServiceProvider
                .GetRequiredService<SlackAgentSelectionService>()
                .RecoverAsync(decided);
            Assert.True(recovery.Completed);
            await recoveryScope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>()
                .MarkCompletedAsync(decided.Id, recovery.State);
        }

        await using var verify = _fixture.Services.CreateAsyncScope();
        var claim = await verify.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>()
            .FindAsync(identity.WorkspaceTeamId, identity.ConversationId, identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Completed, claim!.SelectionState);
        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var inboxRow = await db.SlackProviderInboxRows.SingleAsync(row =>
            row.ConnectionId == selected.Id && row.SlackMessageIdentity.EndsWith(identity.MessageTs));
        Assert.NotNull(inboxRow.DispatchedAt);
        var sessionRow = await db.AgentSessions.SingleAsync(row => row.Id == boundSessionId);
        var session = JSON.Deserialize<AgentSession>(sessionRow.State)!;
        Assert.Single(session.Status.Inputs!, input =>
            input.Id == claim.SelectionInputId
            && input.Text == "survive the inbox crash window");
        Assert.Single(session.Status.Turns!, turn => turn.Id == claim.SelectionTurnId);
        await RefreshAllConnectionLeasesAsync();
    }

    [Fact]
    public async Task Selection_obligation_worker_reaps_only_old_finished_rows()
    {
        var connection = await CreateConnectionAsync("worker-retention", "T-worker-retention", "U_WORKER_RETENTION", "A_WORKER_RETENTION");
        var candidates = new[]
        {
            new SlackSelectionCandidateReference(connection.ProjectId, connection.Id, connection.BotUserId),
            new SlackSelectionCandidateReference(connection.ProjectId, "other-retention-candidate", "U_OTHER_RETENTION"),
        };
        var identities = new Dictionary<string, SlackMessageIdentity>(StringComparer.Ordinal)
        {
            ["pending"] = new("T-worker-retention", "C-worker-retention", "1710000000.030001"),
            ["decided"] = new("T-worker-retention", "C-worker-retention", "1710000000.030002"),
            ["old-completed"] = new("T-worker-retention", "C-worker-retention", "1710000000.030003"),
            ["old-settled"] = new("T-worker-retention", "C-worker-retention", "1710000000.030004"),
            ["recent-completed"] = new("T-worker-retention", "C-worker-retention", "1710000000.030005"),
        };

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            foreach (var pair in identities)
            {
                await prompts.TryClaimAsync(
                    connection.ProjectId,
                    pair.Value.WorkspaceTeamId,
                    pair.Value.ConversationId,
                    pair.Value.MessageTs,
                    null,
                    connection.Id,
                    candidates,
                    "U_WORKER_RETENTION",
                    pair.Key,
                    "[]",
                    SlackAmbiguityKinds.RootMultiMention);
            }
            foreach (var key in new[] { "decided", "old-completed", "recent-completed" })
            {
                var identity = identities[key];
                var ids = SlackChannelLaunchService.PreMintSlackLaunchIds(connection.ProjectId, identity);
                var decision = await prompts.TryDecideAsync(
                    identity.WorkspaceTeamId,
                    identity.ConversationId,
                    identity.MessageTs,
                    connection.ProjectId,
                    connection.Id,
                    SlackSelectionDispatchKinds.RootLaunch,
                    ids.SessionId,
                    ids.InputId,
                    ids.TurnId);
                if (key != "decided")
                    await prompts.MarkCompletedAsync(decision.Snapshot.Id, "accepted");
            }
            var settled = await prompts.FindAsync(
                identities["old-settled"].WorkspaceTeamId,
                identities["old-settled"].ConversationId,
                identities["old-settled"].MessageTs);
            await prompts.TrySettleAsync(settled!.Id, SlackSelectionStates.Pending, "expired");

            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var oldMessageTs = new[]
            {
                identities["old-completed"].MessageTs,
                identities["old-settled"].MessageTs,
            };
            var oldRows = await db.SlackAmbiguousPrompts
                .Where(row => oldMessageTs.Contains(row.MessageTs))
                .ToListAsync();
            foreach (var row in oldRows)
            {
                row.FinishedAt = _fixture.TimeProvider.GetUtcNow().AddMinutes(-31);
                row.UpdatedAt = _fixture.TimeProvider.GetUtcNow().AddMinutes(-31);
            }
            await db.SaveChangesAsync();
        }

        await NewSelectionWorker().ProcessPendingAsync();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var remaining = await verify.ServiceProvider.GetRequiredService<MohistDbContext>()
            .SlackAmbiguousPrompts
            .Where(row => row.WorkspaceTeamId == "T-worker-retention")
            .Select(row => new { row.MessageTs, row.SelectionState })
            .ToListAsync();
        Assert.DoesNotContain(remaining, row => row.MessageTs == identities["old-completed"].MessageTs);
        Assert.DoesNotContain(remaining, row => row.MessageTs == identities["old-settled"].MessageTs);
        Assert.Contains(remaining, row => row.MessageTs == identities["pending"].MessageTs && row.SelectionState == SlackSelectionStates.Pending);
        Assert.Contains(remaining, row => row.MessageTs == identities["decided"].MessageTs && row.SelectionState == SlackSelectionStates.Decided);
        Assert.Contains(remaining, row => row.MessageTs == identities["recent-completed"].MessageTs && row.SelectionState == SlackSelectionStates.Completed);
    }

    [Fact]
    public async Task Selection_obligation_worker_recovers_cross_project_root_once()
    {
        var promptOwner = await CreateConnectionAsync("recovery-owner", "T-recovery", "U_RECOVERY", "A_RECOVERY");
        var selected = await CreateConnectionAsync("recovery-selected", "T-recovery", "U_SELECTED", "B_RECOVERY");
        var identity = new SlackMessageIdentity("T-recovery", "C-recovery", "1710000000.020100");
        var candidates = new[]
        {
            new SlackSelectionCandidateReference(promptOwner.ProjectId, promptOwner.Id, promptOwner.BotUserId),
            new SlackSelectionCandidateReference(selected.ProjectId, selected.Id, selected.BotUserId),
        };

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            await prompts.TryClaimAsync(
                promptOwner.ProjectId,
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                threadTs: null,
                promptOwner.Id,
                candidates,
                "U_RECOVERY",
                "recover in the selected project",
                "[]",
                SlackAmbiguityKinds.RootMultiMention);
            var ids = SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity);
            await prompts.TryDecideAsync(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                selected.ProjectId,
                selected.Id,
                SlackSelectionDispatchKinds.RootLaunch,
                ids.SessionId,
                ids.InputId,
                ids.TurnId);
        }

        var worker = new SlackAgentSelectionObligationWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            _fixture.TimeProvider,
            Options.Create(new SlackProviderOptions()),
            NullLogger<SlackAgentSelectionObligationWorker>.Instance);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await worker.ProcessPendingAsync();
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await worker.ProcessPendingAsync();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var promptsAfter = verify.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await promptsAfter.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Completed, claim!.SelectionState);
        Assert.Equal(selected.ProjectId, claim.ChosenProjectId);
        Assert.Equal(selected.Id, claim.ChosenConnectionId);
        Assert.Equal(
            SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity).SessionId,
            claim.SelectionSessionId);

        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.Id == claim.SelectionSessionId)
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == promptOwner.Id)
            .ToListAsync());
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

    [Fact]
    public async Task Migrated_legacy_claim_with_lost_delivery_is_settled_without_rendering_a_stale_chooser()
    {
        var promptOwner = await CreateConnectionAsync(
            "legacy-redelivery-owner",
            "T-legacy-redelivery",
            "U_LEGACY_REDELIVERY",
            "A_LEGACY_REDELIVERY_OWNER");
        var selected = await CreateConnectionAsync(
            "legacy-redelivery-selected",
            "T-legacy-redelivery",
            "U_LEGACY_SELECTED",
            "A_LEGACY_REDELIVERY_SELECTED");
        var identity = new SlackMessageIdentity(
            promptOwner.WorkspaceTeamId!,
            "C-legacy-redelivery",
            "1710000000.040001");
        var now = _fixture.TimeProvider.GetUtcNow();

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.SlackAmbiguousPrompts.Add(new SlackAmbiguousPromptRow
            {
                Id = $"slkamb_{Guid.NewGuid():N}",
                ProjectId = promptOwner.ProjectId,
                WorkspaceTeamId = identity.WorkspaceTeamId,
                ConversationId = identity.ConversationId,
                MessageTs = identity.MessageTs,
                WinningConnectionId = promptOwner.Id,
                MentionedConnectionIdsJson = JSON.Serialize(new[] { promptOwner.Id, selected.Id }),
                SenderSlackUserId = string.Empty,
                TaskText = string.Empty,
                FilesJson = "[]",
                AmbiguityKind = SlackAmbiguityKinds.Legacy,
                CandidateReferencesJson = "[]",
                SelectionState = SlackSelectionStates.Pending,
                PromptedAt = now.AddMinutes(-1),
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var result = await PostChannelAsync(
            promptOwner,
            identity.ConversationId,
            identity.MessageTs,
            null,
            [promptOwner.BotUserId!, selected.BotUserId!],
            $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> current redelivery",
            promptOwner.OwnerSlackUserId);
        Assert.Equal("ambiguous", result.GetProperty("kind").GetString());
        Assert.Contains("older Agent selection", result.GetProperty("reason").GetString());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var claim = await dbVerify.SlackAmbiguousPrompts.SingleAsync(row =>
            row.WorkspaceTeamId == identity.WorkspaceTeamId
            && row.ConversationId == identity.ConversationId
            && row.MessageTs == identity.MessageTs);
        Assert.Equal(SlackSelectionStates.Settled, claim.SelectionState);
        Assert.Equal("legacy_missing_selection_facts", claim.SettleReason);
        Assert.Empty(await dbVerify.SlackOutboxRows.Where(row =>
            row.DispatchRef == SlackAmbiguousPromptStore.PromptDispatchRef(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs)).ToListAsync());
        Assert.Empty(await dbVerify.AgentJobs.Where(row =>
            row.ProjectId == promptOwner.ProjectId || row.ProjectId == selected.ProjectId).ToListAsync());
        Assert.Empty(await dbVerify.AgentSessions.Where(row =>
            row.LabelConnectionId == promptOwner.Id || row.LabelConnectionId == selected.Id).ToListAsync());
        Assert.Empty(await dbVerify.SlackProviderInboxRows.Where(row =>
            row.ConversationId == identity.ConversationId
            && row.SlackMessageIdentity.EndsWith(identity.MessageTs)).ToListAsync());
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

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text,
        string? senderSlackUserId = null,
        SlackIngressFile[]? files = null)
    {
        var result = await PostChannelAttemptAsync(
            connection,
            conversationId,
            messageTs,
            threadTs,
            mentions,
            text,
            senderSlackUserId,
            files);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        return result.Data!.Value;
    }

    private async Task<(HttpStatusCode Status, JsonElement? Data)> PostChannelAttemptAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text,
        string? senderSlackUserId = null,
        SlackIngressFile[]? files = null)
    {
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions,
            senderSlackUserId = senderSlackUserId ?? connection.OwnerSlackUserId ?? "U_OWNER",
            senderKind = "human",
            text,
            files = files ?? [],
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, null);
        var raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return (response.StatusCode, null);
        var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.GetProperty("data").Clone());
    }

    private async Task<AgentConnection> CreateConnectionAsync(
        string agentNameSuffix,
        string workspaceTeamId,
        string ownerSlackUserId,
        string appId,
        string? projectId = null)
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var resolvedProjectId = projectId ?? $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var existingProject = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == resolvedProjectId);
        if (existingProject is null)
        {
            db.Projects.Add(new ProjectRow
            {
                Id = resolvedProjectId,
                Name = resolvedProjectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        var botUserId = $"U{agentNameSuffix.GetHashCode():X}".PadRight(8, '0').Substring(0, 8);
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = resolvedProjectId,
            Name = $"Mohist Agent {agentNameSuffix}",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = resolvedProjectId,
                Name = $"Mohist Agent {agentNameSuffix}",
                Status = AgentStatus.Active,
                Instructions = "Handle Slack requests.",
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = resolvedProjectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            BotUserId = botUserId,
            BotName = $"Mohist {agentNameSuffix}".Trim(),
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = ownerSlackUserId,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var agentAppId = $"agent_app_{Guid.NewGuid():N}";
        var enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(_fixture, workspaceTeamId);
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = workspaceTeamId,
            AgentConnectionId = id,
            AppId = appId,
            BotUserId = botUserId,
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
        await secrets.StoreAsync(new SecretStoreAddress(resolvedProjectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(resolvedProjectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, resolvedProjectId, id);
        _connectionLeases[id] = leaseId;
        return new AgentConnection
        {
            Id = id,
            ProjectId = resolvedProjectId,
            AgentId = agentId,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            BotUserId = botUserId,
            OwnerSlackUserId = ownerSlackUserId,
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}
