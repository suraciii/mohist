using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    /// <summary>
    /// Owner-only channel state machine. Classifies the message BEFORE
    /// the inbox row is written (514 D5 principle): Bot/unknown senders
    /// and plain unbound-channel messages return without persisting an
    /// inbox row. A binding lookup is reconciled from the inbox route
    /// or Session provenance when missing, so a launch that crashed
    /// between <c>LaunchConnectionAsync</c> and <c>BindAsync</c> still
    /// routes subsequent thread replies to the original session.
    /// <para>
    /// Workspace-scoped multi-Agent attribution (D4) and the once-only
    /// ambiguity prompt (D5) live here. Mention parsing yields the
    /// ordered list of stable Slack user ids the adapter extracted; the
    /// state machine intersects them with the workspace's identity-bound
    /// Bots (<c>M ∩ W</c>) so arbitrary human mentions are never
    /// treated as Bot mentions.
    /// </para>
    /// </summary>
    private static IResult AgentNotFoundResponse() =>
        ApiResults.Fail("The Agent bound to this Connection no longer exists.", 409, "agent_not_found");

    private static IResult AdmissionResponse(SlackAdmissionDecision decision) =>
        ApiResults.Ok(new
        {
            kind = decision.Kind,
            reason = decision.Reason,
            responseOwner = decision.ResponseOwner,
        });

    private static async Task<IResult> HandleChannelIngressAsync(HandleChannelIngressRequest req, CancellationToken ct)
    {
        var body = req.Body;
        var projectId = req.ProjectId;
        var connection = req.Connection;

        var rootTs = !string.IsNullOrWhiteSpace(body.ThreadTs) ? body.ThreadTs : body.MessageTs;
        var mentionedUserIds = BuildMentionedBotIds(body.MentionedUserIds);
        var ownBotUserId = connection.BotUserId ?? string.Empty;

        var workspaceBots = await req.Connections.ListBoundBotsByWorkspaceAsync(body.TeamId, ct);
        var mentionedWorkspaceBots = MentionedWorkspaceBots(mentionedUserIds, workspaceBots);
        var threadBindings = await req.ThreadMapping.ListBindingsByWorkspaceAsync(
            body.TeamId, body.ConversationId, rootTs, ct);

        // The decision for THIS Connection is read once per ingress and
        // reused at the five channel owner-check sites below. Under the
        // default owner_only policy this stays a single equality check
        // (Allow iff sender == Owner) with no Slack API traffic; the
        // other policy branches swap the Allow path but keep the
        // no-cache contract.
        var decision = await req.AccessDecider.EvaluateAsync(
            connection, req.SenderSlackUserId, body.TeamId, body.ConversationId,
            isDirectMessage: false, req.LeaseContext, ct);

        var ingressDecision = SlackChannelIngressPolicy.Decide(
            connection.Id,
            ownBotUserId,
            decision.Allowed,
            decision.Reason,
            isRootMessage: string.IsNullOrWhiteSpace(body.ThreadTs),
            hasThread: !string.IsNullOrWhiteSpace(body.ThreadTs),
            hasPrompt: !string.IsNullOrWhiteSpace(RemoveBotMention(body.Text ?? string.Empty, ownBotUserId)),
            hasFiles: body.Files.Count != 0,
            mentionedWorkspaceBots,
            threadBindings);
        if (ingressDecision.Disposition == SlackChannelIngressDisposition.Ignore)
            return ApiResults.Ok(new { kind = "ignored" });
        if (ingressDecision.Disposition == SlackChannelIngressDisposition.Reject)
            return await RejectAsync(req, ingressDecision.Reason!, ct);

        if (mentionedWorkspaceBots.Count >= 2)
        {
            var routing = SlackMultiAgentRoutingPolicy.Decide(
                connection.Id,
                req.SenderSlackUserId,
                decision.Allowed,
                mentionedWorkspaceBots
                    .Select(bot => new SlackMultiAgentRoutingCandidate(
                        bot.ProjectId, bot.ConnectionId, bot.BotUserId, bot.OwnerSlackUserId))
                    .ToArray())!;
            return routing.Disposition switch
            {
                SlackMultiAgentRoutingDisposition.Ignore => ApiResults.Ok(new { kind = "ignored" }),
                SlackMultiAgentRoutingDisposition.RejectNonOwner =>
                    await HandleAmbiguousNonOwnerAsync(
                        req,
                        routing.Candidates,
                        string.IsNullOrWhiteSpace(body.ThreadTs)
                            ? SlackAmbiguityKinds.RootMultiMention
                            : SlackAmbiguityKinds.ThreadMultiMention,
                        ct),
                SlackMultiAgentRoutingDisposition.Prompt => await HandleAmbiguousPromptAsync(
                    req,
                    routing.Candidates,
                    string.IsNullOrWhiteSpace(body.ThreadTs)
                        ? SlackAmbiguityKinds.RootMultiMention
                        : SlackAmbiguityKinds.ThreadMultiMention,
                    ct),
                _ => throw new InvalidOperationException("Unknown multi-agent routing disposition."),
            };
        }

        if (mentionedWorkspaceBots.Count == 1)
        {
            var addressedBot = mentionedWorkspaceBots[0];
            if (!string.Equals(addressedBot.BotUserId, ownBotUserId, StringComparison.Ordinal))
                return ApiResults.Ok(new { kind = "ignored" });

            var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);
            var isRootMention = string.IsNullOrWhiteSpace(body.ThreadTs);

            var ownBinding = threadBindings.FirstOrDefault(
                binding => string.Equals(binding.ConnectionId, connection.Id, StringComparison.Ordinal));
            var otherBotsInThread = threadBindings.Any(
                binding => !string.Equals(binding.ConnectionId, connection.Id, StringComparison.Ordinal));

            if (!decision.Allowed)
                return await RejectAsync(req, decision.Reason, ct);

            if (ownBinding is not null && !isRootMention)
                return await DispatchChannelFollowupAsync(req, ownBinding.SessionId, prompt, ct);

            if (isRootMention)
            {
                if (string.IsNullOrWhiteSpace(prompt) && body.Files.Count == 0)
                {
                    const string reason = "Please send a task for the Agent to perform.";
                    await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                    return ApiResults.Ok(new { kind = "rejected", reason });
                }
                return await LaunchChannelRootAsync(req, prompt, rootTs, null, ct);
            }

            if (otherBotsInThread)
            {
                if (string.IsNullOrWhiteSpace(prompt) && body.Files.Count == 0)
                {
                    const string reason = "Please send a task for the Agent to perform.";
                    await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                    return ApiResults.Ok(new { kind = "rejected", reason });
                }
                return await LaunchChannelRootAsync(req, prompt, rootTs, null, ct);
            }

            var reconciled = await ReconcileSessionIdAsync(
                req, projectId, body.TeamId, body.ConversationId, rootTs, ct);
            if (reconciled is not null)
                return await DispatchChannelFollowupAsync(req, reconciled, prompt, ct);

            if (string.IsNullOrWhiteSpace(prompt) && body.Files.Count == 0)
            {
                const string reason = "Please send a task for the Agent to perform.";
                await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }

            var historyOutcome = await ReadThreadHistoryIfAnyAsync(req, rootTs, ct);

            if (historyOutcome.Outcome == SlackThreadHistoryReadOutcome.Refused)
            {
                const string reason = "I couldn't read the full thread discussion; please re-mention me in a moment and I'll try again.";
                await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }

            var startupContext = historyOutcome.Outcome == SlackThreadHistoryReadOutcome.Imported
                ? BuildStartupContext(req, historyOutcome.Messages)
                : null;
            return await LaunchChannelRootAsync(req, prompt, rootTs, startupContext, ct);
        }

        if (threadBindings.Count >= 2)
        {
            var bindingConnectionIds = threadBindings
                .Select(binding => binding.ConnectionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var ownerClaimantConnectionId = threadBindings
                .Select(binding => workspaceBots.FirstOrDefault(bot =>
                    string.Equals(bot.ConnectionId, binding.ConnectionId, StringComparison.Ordinal)
                    && string.Equals(bot.OwnerSlackUserId, req.SenderSlackUserId, StringComparison.Ordinal))?.ConnectionId)
                .FirstOrDefault(connectionId => connectionId is not null);
            var currentConnectionIsBound = bindingConnectionIds.Contains(connection.Id, StringComparer.Ordinal);
            var senderAuthorizedForCurrentConnection = decision.Allowed;
            if (!currentConnectionIsBound
                || (ownerClaimantConnectionId is not null
                    && !senderAuthorizedForCurrentConnection
                    && !string.Equals(ownerClaimantConnectionId, connection.Id, StringComparison.Ordinal)))
                return ApiResults.Ok(new { kind = "ignored" });
            var botLookup = workspaceBots.ToDictionary(b => b.ConnectionId, b => b.BotUserId, StringComparer.Ordinal);
            var routingCandidates = threadBindings
                .Select(binding => new SlackMultiAgentRoutingCandidate(
                    binding.ProjectId,
                    binding.ConnectionId,
                    botLookup.TryGetValue(binding.ConnectionId, out var label) ? label : binding.ConnectionId,
                    workspaceBots.FirstOrDefault(bot => string.Equals(bot.ConnectionId, binding.ConnectionId, StringComparison.Ordinal))?.OwnerSlackUserId,
                    binding.SessionId,
                    binding.RootMessageTs))
                .ToArray();
            if (!senderAuthorizedForCurrentConnection)
                return await HandleAmbiguousNonOwnerAsync(
                    req,
                    routingCandidates,
                    SlackAmbiguityKinds.MultiBoundThreadReply,
                    ct);
            return await HandleAmbiguousPromptAsync(
                req,
                routingCandidates,
                SlackAmbiguityKinds.MultiBoundThreadReply,
                ct);
        }

        if (threadBindings.Count == 1)
        {
            var binding = threadBindings[0];
            if (!string.Equals(binding.ConnectionId, connection.Id, StringComparison.Ordinal))
                return ApiResults.Ok(new { kind = "ignored" });

            var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);

            if (!decision.Allowed)
                return await RejectAsync(req, decision.Reason, ct);

            return await DispatchChannelFollowupAsync(req, binding.SessionId, prompt, ct);
        }

        if (!string.IsNullOrWhiteSpace(body.ThreadTs))
        {
            var reconciled = await ReconcileSessionIdAsync(
                req, projectId, body.TeamId, body.ConversationId, rootTs, ct);
            if (reconciled is not null)
            {
                var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);

                if (!decision.Allowed)
                    return await RejectAsync(req, decision.Reason, ct);
                return await DispatchChannelFollowupAsync(req, reconciled, prompt, ct);
            }
        }

        return ApiResults.Ok(new { kind = "ignored" });
    }

    /// <summary>
    /// Reconciles a session id for the inbound thread when no binding
    /// row is present. Order:
    /// <list type="number">
    /// <item><description>the inbox route whose message identity equals the thread root (the launch path persists the session id BEFORE the reply per D2);</description></item>
    /// <item><description>the unique AgentSession row whose provenance labels match (connection, conversation, root message ts).</description></item>
    /// </list>
    /// When both recovery sources agree, the binding row is repaired
    /// so subsequent lookups stay index-only.
    /// </summary>
    private static async Task<string?> ReconcileSessionIdAsync(
        HandleChannelIngressRequest req,
        string projectId,
        string workspaceTeamId,
        string conversationId,
        string rootTs,
        CancellationToken ct)
    {
        var inboxSessionId = await ResolveInboxRootSessionIdAsync(
            req, projectId, req.Connection.Id, workspaceTeamId, conversationId, rootTs, ct);
        if (!string.IsNullOrWhiteSpace(inboxSessionId))
        {
            await req.ThreadMapping.UpsertAsync(
                projectId, workspaceTeamId, req.Connection.Id, conversationId, rootTs,
                req.SenderSlackUserId, inboxSessionId, rootTs, ct);
            return inboxSessionId;
        }

        var provenanceSessionId = await ResolveSessionProvenanceAsync(
            req, projectId, req.Connection.Id, workspaceTeamId, conversationId, rootTs, ct);
        if (!string.IsNullOrWhiteSpace(provenanceSessionId))
        {
            await req.ThreadMapping.UpsertAsync(
                projectId, workspaceTeamId, req.Connection.Id, conversationId, rootTs,
                req.SenderSlackUserId, provenanceSessionId, rootTs, ct);
            return provenanceSessionId;
        }

        return null;
    }

    private static async Task<string?> ResolveInboxRootSessionIdAsync(
        HandleChannelIngressRequest req,
        string projectId,
        string connectionId,
        string workspaceTeamId,
        string conversationId,
        string threadTs,
        CancellationToken ct)
    {
        await using var scope = req.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<SlackProviderInboxStore>();
        var root = await inbox.FindRootRouteSessionIdAsync(
            projectId, connectionId, workspaceTeamId, conversationId, threadTs, ct);
        return root;
    }

    private static async Task<string?> ResolveSessionProvenanceAsync(
        HandleChannelIngressRequest req,
        string projectId,
        string connectionId,
        string workspaceTeamId,
        string conversationId,
        string threadTs,
        CancellationToken ct)
    {
        return await req.Sessions.FindSessionIdBySlackThreadProvenanceAsync(
            projectId, connectionId, conversationId, threadTs, ct);
    }

    /// <summary>
    /// Filters the parsed mention list down to the subset that maps to
    /// identity-bound Mohist Bots in the same workspace. The result is
    /// the <c>M ∩ W</c> set D4 uses to attribute channel messages —
    /// arbitrary human mentions are never treated as Bot mentions, and
    /// a Bot managed by another Mohist Server never appears here.
    /// Deduplicates by <c>BotUserId</c> so multiple Connections bound to
    /// the same Bot (a test setup convenience or a future multi-workspace
    /// Bot) never collapse a single-Bot mention into a multi-Bot prompt.
    /// </summary>
    private static IReadOnlyList<WorkspaceBoundBot> MentionedWorkspaceBots(
        IReadOnlyList<string> mentionedUserIds,
        IReadOnlyList<WorkspaceBoundBot> workspaceBots)
    {
        if (mentionedUserIds.Count == 0 || workspaceBots.Count == 0)
            return Array.Empty<WorkspaceBoundBot>();
        var mentionedSet = new HashSet<string>(mentionedUserIds, StringComparer.Ordinal);
        var result = new List<WorkspaceBoundBot>(workspaceBots.Count);
        var seenBotIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bot in workspaceBots)
        {
            if (!mentionedSet.Contains(bot.BotUserId))
                continue;
            if (!seenBotIds.Add(bot.BotUserId))
                continue;
            result.Add(bot);
        }
        return result;
    }

    private static string RemoveBotMentions(string text, IEnumerable<string> botUserIds)
    {
        var ids = botUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
            return text.Trim();
        return SlackMentionToken.Replace(text.Trim(), match =>
            ids.Contains(match.Groups["id"].Value) ? string.Empty : match.Value).Trim();
    }

    private static IReadOnlyList<string> BuildMentionedBotIds(IReadOnlyList<string>? mentioned)
    {
        if (mentioned is null || mentioned.Count == 0) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(mentioned.Count);
        foreach (var id in mentioned)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Claims and posts the once-only "pick a single Agent" prompt for
    /// an ambiguous channel message. The race-winning Connection
    /// (D5 first-writer-wins on
    /// <c>(WorkspaceTeamId, ConversationId, MessageTs)</c>) enqueues a
    /// UserAction reply via its own outbox; every loser observes the
    /// row exists and no-ops, so concurrent per-Connection ingress
    /// calls and Slack redeliveries collapse to one prompt. The prompt
    /// copies the inbound <c>ThreadTs</c> onto the delivery so a root
    /// ambiguous message is prompted at the channel root and a thread
    /// ambiguous reply is prompted in the same thread.
    /// </summary>
    private static async Task<IResult> HandleAmbiguousPromptAsync(
        HandleChannelIngressRequest req,
        IReadOnlyList<SlackMultiAgentRoutingCandidate> candidates,
        string ambiguityKind,
        CancellationToken ct)
    {
        var body = req.Body;
        var projectId = req.ProjectId;
        var connection = req.Connection;

        var labelSummary = string.Join(", ", candidates.Select(candidate => candidate.BotUserId));
        var promptText = $"Multiple Agents could answer this: {labelSummary}. Re-mention a single Bot explicitly to proceed.";
        var dispatchRef = SlackAmbiguousPromptStore.PromptDispatchRef(
            body.TeamId, body.ConversationId, body.MessageTs);
        var candidateReferences = candidates
            .Select(candidate => new SlackSelectionCandidateReference(
                candidate.ProjectId,
                candidate.ConnectionId,
                candidate.BotUserId))
            .ToArray();
        var taskText = RemoveBotMentions(
            body.Text ?? string.Empty,
            candidates.Select(candidate => candidate.BotUserId));
        var filesJson = JSON.Serialize(body.Files);

        var claim = await req.AmbiguousPrompts.TryClaimAsync(
            projectId, body.TeamId, body.ConversationId, body.MessageTs,
            body.ThreadTs, connection.Id, candidateReferences,
            req.SenderSlackUserId, taskText, filesJson, ambiguityKind, ct);

        if (!claim.Claimed)
            return ApiResults.Ok(new { kind = "ambiguous", reason = "Another Bot is responding.", winner = claim.WinningConnectionId });
        if (claim.Snapshot.SelectionState != SlackSelectionStates.Pending)
            return ApiResults.Ok(new { kind = "ambiguous", reason = "This Agent selection is no longer active." });

        var signer = req.Services.GetRequiredService<ISlackActionSigner>();
        var expiresAt = req.Services.GetRequiredService<TimeProvider>().GetUtcNow()
            .Add(SlackSelectionActionPayload.Lifetime);
        var blocks = await SlackSelectionChooserRenderer.BuildBlocksAsync(
            signer,
            connection,
            body.TeamId,
            body.ConversationId,
            body.MessageTs,
            body.ThreadTs,
            req.SenderSlackUserId,
            ambiguityKind,
            candidateReferences,
            candidates.Select(candidate => candidate.BotUserId).ToArray(),
            expiresAt,
            ct);
        await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
            promptText, dispatchRef, ct, body.ThreadTs, blocks);
        return ApiResults.Ok(new { kind = "ambiguous", reason = promptText });
    }

    private static async Task<IResult> RejectAsync(
        HandleChannelIngressRequest req,
        string reason,
        CancellationToken ct)
    {
        await EnqueueReplyAsync(req.Outbox, req.ProjectId, req.Connection, req.Body.ConversationId,
            reason, null, ct, req.Body.ThreadTs);
        return ApiResults.Ok(new { kind = "rejected", reason });
    }

    private static async Task<IResult> HandleAmbiguousNonOwnerAsync(
        HandleChannelIngressRequest req,
        IReadOnlyList<SlackMultiAgentRoutingCandidate> candidates,
        string ambiguityKind,
        CancellationToken ct)
    {
        var body = req.Body;
        var claim = await req.AmbiguousPrompts.TryClaimAsync(
            req.ProjectId,
            body.TeamId,
            body.ConversationId,
            body.MessageTs,
            body.ThreadTs,
            req.Connection.Id,
            candidates.Select(candidate => new SlackSelectionCandidateReference(
                candidate.ProjectId,
                candidate.ConnectionId,
                candidate.BotUserId)).ToArray(),
            req.SenderSlackUserId,
            RemoveBotMentions(body.Text ?? string.Empty, candidates.Select(candidate => candidate.BotUserId)),
            JSON.Serialize(body.Files),
            ambiguityKind,
            ct);
        if (!claim.Claimed || claim.Snapshot.SelectionState != SlackSelectionStates.Pending)
            return ApiResults.Ok(new { kind = "ignored" });

        const string reason = "This Slack Connection is available only to its owner.";
        await EnqueueRequiredReplyAsync(
            req.Outbox,
            req.ProjectId,
            req.Connection,
            body.ConversationId,
            reason,
            SlackAmbiguousPromptStore.PromptDispatchRef(body.TeamId, body.ConversationId, body.MessageTs),
            ct,
            body.ThreadTs);
        return ApiResults.Ok(new { kind = "rejected", reason });
    }

    private static async Task<IResult> LaunchChannelRootAsync(
        HandleChannelIngressRequest req,
        string prompt,
        string rootTs,
        AgentStartupContext? startupContext,
        CancellationToken ct)
    {
        var body = req.Body;
        var result = await req.Services.GetRequiredService<SlackChannelLaunchService>().LaunchAsync(
            new SlackChannelLaunchRequest(
                req.ProjectId,
                req.Connection,
                req.Identity,
                req.SenderSlackUserId,
                prompt,
                body.Files,
                rootTs,
                body.ThreadTs,
                ToServiceLaunchIds(SlackChannelLaunchService.PreMintSlackLaunchIds(req.ProjectId, req.Identity)),
                startupContext,
                req.ThreadMapping),
            ct);

        if (result.BoundSessionId is not null)
            return await DispatchChannelFollowupAsync(req, result.BoundSessionId, prompt, ct);
        if (result.Kind == "agent_not_found")
            return AgentNotFoundResponse();
        if (result.Conflict)
            return ApiResults.Conflict(result.Reason!, result.Kind);
        if (result.ResponseOwner is not null || result.Kind == "rejected")
            return ApiResults.Ok(new
            {
                kind = result.Kind,
                reason = result.Reason,
                responseOwner = result.ResponseOwner,
            });
        return ApiResults.Ok(new
        {
            kind = result.Kind,
            sessionId = result.SessionId,
            jobKey = result.JobKey,
            inputId = result.InputId,
            turnId = result.TurnId,
            threadRoot = result.ThreadRoot ?? rootTs,
        });
    }

    private static SlackChannelLaunchServiceLaunchIds ToServiceLaunchIds(
        (string SessionId, string InputId, string TurnId) ids) =>
        new(ids.SessionId, ids.InputId, ids.TurnId);

}
