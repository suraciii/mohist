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

}
