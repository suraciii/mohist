using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Slack;
using Mohist.Server.Workspace.Services;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Owns the idempotent Slack channel launch sequence. The request contains
/// every message and ownership fact needed to launch, so the same sequence
/// can later be used by an interaction selection without borrowing the
/// delivering request's Project or Connection context.
/// </summary>
internal sealed class SlackChannelLaunchService : IScopedService
{
    private readonly AgentConnectionStore _connections;
    private readonly AgentQuerier _agents;
    private readonly SlackAdmissionService _admission;
    private readonly SlackThreadLaunchReservationStore _threadLaunchReservations;
    private readonly SlackProviderInboxStore _inbox;
    private readonly SlackAttachmentInputBinder _attachmentBinder;
    private readonly InteractionWorkspaceProvisioner _workspaceProvisioner;
    private readonly IAgentLauncher _launcher;
    private readonly IGrainFactory _grains;
    private readonly SlackStatusProjection _status;
    private readonly SlackTurnControlService _turnControl;
    private readonly SlackWebLinkBuilder _webLinks;
    private readonly ProjectQuerier _projects;
    private readonly TimeProvider _time;
    private readonly SlackOutboxStore _outbox;

    public SlackChannelLaunchService(
        AgentConnectionStore connections,
        AgentQuerier agents,
        SlackAdmissionService admission,
        SlackThreadLaunchReservationStore threadLaunchReservations,
        SlackProviderInboxStore inbox,
        SlackAttachmentInputBinder attachmentBinder,
        InteractionWorkspaceProvisioner workspaceProvisioner,
        IAgentLauncher launcher,
        IGrainFactory grains,
        SlackStatusProjection status,
        SlackTurnControlService turnControl,
        SlackWebLinkBuilder webLinks,
        ProjectQuerier projects,
        TimeProvider time,
        SlackOutboxStore outbox)
    {
        _connections = connections;
        _agents = agents;
        _admission = admission;
        _threadLaunchReservations = threadLaunchReservations;
        _inbox = inbox;
        _attachmentBinder = attachmentBinder;
        _workspaceProvisioner = workspaceProvisioner;
        _launcher = launcher;
        _grains = grains;
        _status = status;
        _turnControl = turnControl;
        _webLinks = webLinks;
        _projects = projects;
        _time = time;
        _outbox = outbox;
    }

    public async Task<SlackChannelLaunchResult> LaunchAsync(
        SlackChannelLaunchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = await _agents.GetByIdAsync(request.ProjectId, request.Connection.AgentId, ct);
        if (agent is null)
            return SlackChannelLaunchResult.AgentNotFound;

        var admission = await _admission.AdmitNewWorkAsync(
            request.ProjectId,
            request.Connection,
            agent,
            request.Identity,
            request.ThreadAnchor,
            ct);
        if (!admission.Admitted)
        {
            return new SlackChannelLaunchResult(
                admission.Kind,
                Reason: admission.Reason,
                ResponseOwner: admission.ResponseOwner);
        }

        if (IsBackpressured(request.Connection))
        {
            return new SlackChannelLaunchResult(
                "backpressured",
                Reason: SlackAdmissionMessages.Backpressured,
                ResponseOwner: SlackIngressResponseOwners.Adapter);
        }

        var dispatchRef = $"slack-thread:{request.Identity.WorkspaceTeamId}:{request.Identity.ConversationId}:{request.ThreadAnchor}";
        var reservation = await _threadLaunchReservations.ReserveAsync(
            request.ProjectId,
            request.Identity.WorkspaceTeamId,
            request.Connection.Id,
            request.Identity.ConversationId,
            request.ThreadAnchor,
            request.Identity.MessageTs,
            request.SenderSlackUserId,
            ct);
        if (reservation.Kind == SlackThreadLaunchReservationKind.InProgress)
        {
            return new SlackChannelLaunchResult(
                "slack_thread_launch_in_progress",
                Reason: "Another launch is already being established for this Slack thread; retry this message.",
                Conflict: true);
        }
        if (reservation.Kind == SlackThreadLaunchReservationKind.Bound)
        {
            await request.ThreadMapping.UpsertAsync(
                request.ProjectId,
                request.Identity.WorkspaceTeamId,
                request.Connection.Id,
                request.Identity.ConversationId,
                request.ThreadAnchor,
                request.SenderSlackUserId,
                reservation.SessionId!,
                request.ThreadAnchor,
                ct);
            return new SlackChannelLaunchResult(
                "bound",
                BoundSessionId: reservation.SessionId);
        }

        SlackProviderInboxAcceptResult accepted;
        try
        {
            accepted = await _inbox.AcceptAsync(new SlackProviderInboxDraft(
                request.ProjectId,
                request.Connection.Id,
                request.Identity,
                request.SenderSlackUserId,
                request.ThreadAnchor),
                new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.LaunchThread),
                ct);
        }
        catch (SlackProviderInboxCapacityExceededException)
        {
            return new SlackChannelLaunchResult(
                "backpressured",
                Reason: SlackAdmissionMessages.Backpressured,
                ResponseOwner: SlackIngressResponseOwners.Adapter);
        }

        if (!accepted.AlreadyExisted)
            await _connections.ClearOfflineGapIfSetAsync(request.ProjectId, request.Connection.Id, ct);

        AgentLaunchResult? launch = null;
        var existingRoute = accepted.AlreadyExisted
            ? await _inbox.GetRouteAsync(request.ProjectId, accepted.Id, ct)
            : null;
        var sessionId = existingRoute?.SessionId ?? reservation.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var attachmentBinding = await _attachmentBinder.PrepareAsync(
                request.ProjectId,
                request.Connection,
                request.Identity,
                request.PreMintedLaunchIds.SessionId,
                request.PreMintedLaunchIds.InputId,
                request.Files,
                ct);
            if (string.IsNullOrWhiteSpace(request.Prompt) && attachmentBinding.AcceptedCount == 0)
            {
                await _attachmentBinder.RollbackAsync(
                    request.ProjectId,
                    request.PreMintedLaunchIds.SessionId,
                    request.PreMintedLaunchIds.InputId,
                    attachmentBinding,
                    CancellationToken.None);
                var rejection = BuildAttachmentAck(
                    "No usable file was accepted, so the task was not started.",
                    request.Files,
                    attachmentBinding);
                await _outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
                    request.ProjectId,
                    request.Connection.Id,
                    request.Identity.WorkspaceTeamId,
                    request.Identity.ConversationId,
                    SlackOutboxKinds.UserAction,
                    dispatchRef,
                    JsonSerializer.Serialize(new { text = rejection }),
                    request.ThreadAnchor),
                    ct);
                await _inbox.MarkDispatchedAsync(request.ProjectId, accepted.Id, ct);
                return new SlackChannelLaunchResult("rejected", Reason: rejection);
            }

            try
            {
                var workspaceName = await _workspaceProvisioner.EnsureSlackWorkspaceAsync(
                    request.ProjectId,
                    request.Identity.WorkspaceTeamId,
                    request.Identity.ConversationId,
                    _time.GetUtcNow());
                launch = await _launcher.LaunchConnectionAsync(
                    agent,
                    request.Prompt,
                    new ConnectionLaunchOrigin(
                        request.Connection.Id,
                        request.Identity.WorkspaceTeamId,
                        request.SenderSlackUserId,
                        request.Identity.ConversationId,
                        request.Identity.MessageTs,
                        request.ThreadAnchor),
                    workspaceName: workspaceName,
                    startupContext: request.StartupContext,
                    attachments: attachmentBinding.AcceptedDescriptors,
                    attachmentIds: attachmentBinding.AttachmentIds,
                    preMintedSessionId: request.PreMintedLaunchIds.SessionId,
                    preMintedInputId: request.PreMintedLaunchIds.InputId,
                    preMintedTurnId: request.PreMintedLaunchIds.TurnId,
                    ct: ct);
            }
            catch
            {
                await _attachmentBinder.RollbackAsync(
                    request.ProjectId,
                    request.PreMintedLaunchIds.SessionId,
                    request.PreMintedLaunchIds.InputId,
                    attachmentBinding,
                    CancellationToken.None);
                throw;
            }
            sessionId = launch.SessionId;
        }

        if (existingRoute?.SessionId is null)
        {
            sessionId = await _inbox.SetRouteSessionIdAsync(
                request.ProjectId,
                accepted.Id,
                sessionId!,
                ct);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var bindResult = await request.ThreadMapping.UpsertAsync(
                request.ProjectId,
                request.Identity.WorkspaceTeamId,
                request.Connection.Id,
                request.Identity.ConversationId,
                request.ThreadAnchor,
                request.SenderSlackUserId,
                sessionId,
                request.ThreadAnchor,
                ct);
            sessionId = bindResult.SessionId;
            if (bindResult.AlreadyExisted)
            {
                sessionId = await _inbox.SetRouteSessionIdAsync(
                    request.ProjectId,
                    accepted.Id,
                    sessionId,
                    ct);
            }
            await _threadLaunchReservations.BindSessionAsync(
                request.ProjectId,
                request.Identity.WorkspaceTeamId,
                request.Connection.Id,
                request.Identity.ConversationId,
                request.ThreadAnchor,
                sessionId,
                ct);
        }

        await _status.EnqueueReceivedAsync(
            request.ProjectId,
            request.Connection.Id,
            request.Identity,
            request.ThreadTs,
            ct);
        if (launch is not null)
        {
            await EnqueueInitialLaunchStatusAsync(
                request.ProjectId,
                request.Connection,
                request.Identity,
                request.ThreadAnchor,
                launch,
                request.SenderSlackUserId,
                ct);
        }
        await _inbox.MarkDispatchedAsync(request.ProjectId, accepted.Id, ct);
        return new SlackChannelLaunchResult(
            accepted.AlreadyExisted ? "queued" : "accepted",
            SessionId: sessionId,
            JobKey: launch?.JobKey,
            InputId: launch?.InputId,
            TurnId: launch?.TurnId,
            ThreadRoot: request.ThreadAnchor);
    }

    internal async Task EnqueueInitialLaunchStatusAsync(
        string projectId,
        AgentConnection connection,
        SlackMessageIdentity source,
        string? threadTs,
        AgentLaunchResult launch,
        string actorSlackUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(launch.SessionId) || string.IsNullOrWhiteSpace(launch.TurnId))
            return;

        var turn = await _grains.GetGrain<IAgentSessionGrain>(launch.SessionId)
            .ResolveTurnControlAsync(launch.TurnId);
        if (turn is null
            || turn.Classification is not (AgentTurnControlClassification.Queued or AgentTurnControlClassification.Executing))
        {
            return;
        }

        var stopAction = await _turnControl.CreateStopActionAsync(
            connection,
            launch.SessionId,
            launch.TurnId,
            launch.InputId,
            SlackStatusProjection.DispatchRef(source, "progress"),
            actorSlackUserId,
            source,
            threadTs,
            ct);
        var blocks = await BuildSessionStatusBlocksAsync(projectId, launch.SessionId, stopAction?.Blocks);
        await _status.EnqueueWorkingAsync(
            projectId,
            connection.Id,
            source,
            threadTs,
            SlackStatusProjection.DispatchRef(source, "progress"),
            blocks,
            sessionId: launch.SessionId,
            ct: ct);
    }

    internal static (string SessionId, string InputId, string TurnId) PreMintSlackLaunchIds(
        string projectId,
        SlackMessageIdentity identity)
    {
        var ownershipIdentity = $"{projectId}\nslack:{identity.WorkspaceTeamId}:{identity.ConversationId}:{identity.MessageTs}";
        return (
            $"agent-session-{AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\nsession")}",
            AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\ninput"),
            AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\nturn"));
    }

    internal static string BuildAttachmentAck(
        string acknowledgement,
        IReadOnlyList<SlackIngressFile> files,
        SlackAttachmentBinding? binding)
    {
        if (binding is null || binding.Results.Count == 0)
            return acknowledgement;

        var accepted = binding.Results
            .Where(result => result.IsAccepted && result.Descriptor is not null)
            .Select(result => result.Descriptor!.OriginalFileName)
            .ToArray();
        var rejected = binding.Results
            .Select((result, index) => (Result: result, File: files[index]))
            .Where(item => !item.Result.IsAccepted)
            .Select(item => $"{item.File.Name} ({item.Result.RejectionReason}: {item.Result.RejectionMessage})")
            .ToArray();
        var parts = new List<string> { acknowledgement };
        if (accepted.Length > 0)
            parts.Add($"Files received: {string.Join(", ", accepted)}.");
        if (rejected.Length > 0)
            parts.Add($"Files not used: {string.Join("; ", rejected)}.");
        return string.Join(' ', parts);
    }

    private async Task<JsonElement?> BuildSessionStatusBlocksAsync(
        string projectId,
        string sessionId,
        JsonElement? controlBlocks)
    {
        if (!_webLinks.HasUsableExternalWebUrl)
            return controlBlocks;

        var project = await _projects.GetByIdAsync(projectId);
        var link = project is null
            ? null
            : _webLinks.BuildOpenSession(project.Name, sessionId);
        return CombineBlocks(controlBlocks, link?.Blocks);
    }

    private static JsonElement? CombineBlocks(JsonElement? first, JsonElement? second)
    {
        var blocks = new List<JsonElement>();
        AddBlockArray(blocks, first);
        AddBlockArray(blocks, second);
        return blocks.Count == 0 ? null : JsonSerializer.SerializeToElement(blocks);
    }

    private static void AddBlockArray(List<JsonElement> target, JsonElement? source)
    {
        if (source is not { ValueKind: JsonValueKind.Array })
            return;
        target.AddRange(source.Value.EnumerateArray().Select(block => block.Clone()));
    }

    private static bool IsBackpressured(AgentConnection connection) =>
        connection.ConnectionHealth == ConnectionHealthKind.Degraded
        && SlackConnectionBackpressureReasons.IsBackpressureReason(connection.HealthReason);
}

internal sealed record SlackChannelLaunchRequest(
    string ProjectId,
    AgentConnection Connection,
    SlackMessageIdentity Identity,
    string SenderSlackUserId,
    string Prompt,
    IReadOnlyList<SlackIngressFile> Files,
    string ThreadAnchor,
    string? ThreadTs,
    SlackChannelLaunchServiceLaunchIds PreMintedLaunchIds,
    AgentStartupContext? StartupContext,
    SlackThreadSessionMappingStore ThreadMapping);

internal sealed record SlackChannelLaunchServiceLaunchIds(
    string SessionId,
    string InputId,
    string TurnId);

internal sealed record SlackChannelLaunchResult(
    string Kind,
    string? Reason = null,
    string? ResponseOwner = null,
    string? SessionId = null,
    string? JobKey = null,
    string? InputId = null,
    string? TurnId = null,
    string? ThreadRoot = null,
    string? BoundSessionId = null,
    bool Conflict = false)
{
    public static SlackChannelLaunchResult AgentNotFound { get; } = new("agent_not_found");
}
