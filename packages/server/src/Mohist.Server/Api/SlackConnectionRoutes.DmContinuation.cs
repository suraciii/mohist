using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private static async Task<FollowupRouteResult> RouteFollowupAsync(
        string projectId,
        Agent.Domain.AgentConnection connection,
        SlackMessageIdentity identity,
        IReadOnlyList<SlackIngressFile> files,
        string currentSessionId,
        string prompt,
        string idempotencyKey,
        AgentSessionInputProvenance provenance,
        SlackAttachmentInputBinder attachmentBinder,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followupDispatcher,
        bool allowPendingInitialLaunch,
        CancellationToken ct)
    {
        var preMintedInputId = AgentLaunchCoordinatorCodec.StableToken(
            $"{currentSessionId}\n{idempotencyKey}\nfollowup-input");
        var attachmentBinding = await attachmentBinder.PrepareAsync(
            projectId,
            connection,
            identity,
            currentSessionId,
            preMintedInputId,
            files,
            ct);
        if (string.IsNullOrWhiteSpace(prompt) && attachmentBinding.AcceptedCount == 0)
        {
            await attachmentBinder.RollbackAsync(
                projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult(
                "followup_rejected", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }

        var grain = grains.GetGrain<IAgentSessionGrain>(currentSessionId);
        AgentSessionFollowupAcceptResult accept;
        try
        {
            accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: prompt,
                Source: "agent-session-followup",
                IdempotencyKey: idempotencyKey,
                Attachments: attachmentBinding.AcceptedDescriptors,
                PreMintedInputId: preMintedInputId,
                AttachmentResults: attachmentBinding.Results,
                Provenance: provenance,
                AllowPendingInitialLaunch: allowPendingInitialLaunch));
        }
        catch (RuntimeSessionMissingException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("runtime_session_missing", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (RecoveryOperationInProgressException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("recovery_in_progress", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (AgentSessionFollowupCapacityExceededException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("capacity_exceeded", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (StopOperationInProgressException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("stop_in_progress", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (SessionActivityUnknownException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("session_activity_unknown", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (FollowupConcurrencyLimitException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("concurrency_limit", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (InvalidOperationException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("followup_rejected", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }

        await followupDispatcher.DispatchNextAsync(projectId, currentSessionId, ct);

        if (accept.AlreadyAccepted)
            return new FollowupRouteResult("already_accepted", "already_accepted", currentSessionId, accept.InputId, accept.TurnId, attachmentBinding);
        var status = accept.TurnStatus switch
        {
            AgentTurnStatus.Executing => "executing",
            _ => "queued",
        };
        return new FollowupRouteResult("accepted", status, currentSessionId, accept.InputId, accept.TurnId, attachmentBinding);
    }

    private static async Task<SlackProviderInboxRoute> RecoverRetrySafeDmLaunchAsync(
        HandleDmIngressRequest req,
        SlackProviderInboxAcceptResult accepted,
        SlackProviderInboxRoute route,
        CancellationToken ct)
    {
        if (route.Kind != SlackProviderInboxRouteKinds.Followup
            || string.IsNullOrWhiteSpace(route.SessionId))
        {
            return route;
        }

        var sourceSession = req.Grains.GetGrain<IAgentSessionGrain>(route.SessionId);
        var session = await sourceSession.GetAsync();
        if (session is null || !string.IsNullOrWhiteSpace(session.AgentSessionId))
            return route;

        var initial = await sourceSession.GetInitialLaunchAsync();
        if (initial?.Turn is not { Status: AgentTurnStatus.Failed } failedTurn
            || !AgentSessionRetryPolicy.IsRetryable(failedTurn.Result?.FailureCategory))
        {
            return route;
        }

        var retryService = req.Services.GetRequiredService<AgentSessionRetryService>();
        var retry = await retryService.RetryAsync(
            new AgentSessionRetryCommand(
                req.ProjectId,
                route.SessionId,
                failedTurn.Id,
                $"slack-continuation:auto:{failedTurn.Id}",
                new AgentSessionInputProvenance(
                    "slack",
                    req.Body.TeamId,
                    req.Body.ConversationId,
                    req.Body.ThreadTs ?? req.Body.MessageTs,
                    req.SenderSlackUserId,
                    req.Body.MessageTs,
                    req.Connection.Id,
                    req.Body.ThreadTs ?? req.Body.MessageTs)),
            ct);
        if (retry.Outcome == AgentSessionRetryOutcome.AcceptedPending
            && !string.IsNullOrWhiteSpace(retry.OperationId))
        {
            retry = await retryService.DispatchPendingAsync(req.ProjectId, retry.OperationId, ct);
        }
        if (!retry.IsAccepted || string.IsNullOrWhiteSpace(retry.SessionId))
            return route;

        var replacement = await req.Grains.GetGrain<IAgentSessionGrain>(retry.SessionId).GetAsync();
        if (replacement is null)
        {
            throw new InvalidOperationException(
                $"Slack continuation retry '{retry.OperationId}' has not materialized its replacement Session yet.");
        }

        var replacementSessionId = await req.DmMapping.ReplaceCurrentSessionAndInboxRouteAsync(
            req.ProjectId,
            req.Connection.Id,
            req.Body.TeamId,
            req.SenderSlackUserId,
            req.Body.ConversationId,
            accepted.Id,
            route.SessionId,
            retry.SessionId,
            req.Body.MessageTs,
            ct);
        return route with { SessionId = replacementSessionId };
    }
}
