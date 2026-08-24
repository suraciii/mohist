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

}
