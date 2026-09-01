using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack;

[Subscription(
    Type = EventCatalog.ReverseDns.AgentJobTerminalDelivery,
    Identity = "Mohist.Server.Infrastructure.Slack.SlackTerminalDeliveryHandler")]
public sealed class SlackTerminalDeliveryHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlackTerminalDeliveryHandler> _log;

    public SlackTerminalDeliveryHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<SlackTerminalDeliveryHandler> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt.Data is { ValueKind: JsonValueKind.Object };

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        var delivery = evt.Data?.Deserialize<SlackTerminalDelivery>(CloudEvent.JsonOptions)
            ?? throw new InvalidOperationException("Terminal delivery event has no valid payload.");
        delivery.Validate();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var projectId = delivery.ResolveProjectId(evt.Extensions);
        var projection = scope.ServiceProvider.GetService<SlackStatusProjection>() ?? new SlackStatusProjection(outbox);
        var source = new SlackMessageIdentity(
            delivery.WorkspaceTeamId,
            delivery.ConversationId,
            delivery.MessageTs ?? $"terminal:{delivery.JobKey}");
        // Manager progress is keyed by the immutable Slack origin, just like
        // the ordinary status projection. Terminal delivery never reads or
        // renders assistant text; the Agent reply action owns all text.
        var progressDispatchRef = string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
            ? SlackStatusProjection.DispatchRef(source, "progress")
            : delivery.JobKey.StartsWith("agent-session-followup:", StringComparison.Ordinal)
                ? $"{delivery.JobKey}:progress"
                : null;
        if (ShouldRenderRetry(projectId, delivery))
        {
            SlackRetryAction? retry = null;
            try
            {
                var connection = await scope.ServiceProvider
                    .GetRequiredService<AgentConnectionStore>()
                    .GetAsync(projectId, delivery.ConnectionId, ct);
                if (connection is not null)
                {
                    retry = await scope.ServiceProvider
                        .GetRequiredService<SlackRetryActionService>()
                        .CreateRetryActionAsync(
                            connection,
                            delivery.SessionId!,
                            delivery.TurnId!,
                            source,
                            delivery.ThreadTs,
                            ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Terminal presentation must not turn a missing or
                // temporarily unavailable durable Session/Turn into a lost
                // liveness update. The signed action is optional; reactions
                // remain the safe fallback until the durable facts can be
                // resolved by a later delivery attempt.
                _log.LogWarning(
                    ex,
                    "Could not resolve retry facts for terminal AgentJob {JobKey}; falling back to reaction-only presentation",
                    delivery.JobKey);
            }
            if (retry is not null)
            {
                await projection.EnqueueFailureAsync(
                    projectId,
                    delivery.ConnectionId,
                    source,
                    delivery.ThreadTs ?? delivery.MessageTs,
                    FailureNoticeText(delivery),
                    failureDispatchRef: delivery.JobKey,
                    progressDispatchRef: progressDispatchRef,
                    blocks: retry.Blocks,
                    ct: ct);
            }
            else
            {
                await projection.FinalizeLivenessAsync(
                    projectId,
                    delivery.ConnectionId,
                    source,
                    delivery.ThreadTs ?? delivery.MessageTs,
                    delivery.Status,
                    progressDispatchRef,
                    ct);
            }
        }
        else
        {
            await projection.FinalizeLivenessAsync(
                projectId,
                delivery.ConnectionId,
                source,
                delivery.ThreadTs ?? delivery.MessageTs,
                delivery.Status,
                progressDispatchRef,
                ct);
        }

        _log.LogInformation(
            "Finalized Slack liveness for AgentJob {JobKey} on connection {ConnectionId} (reply body is owned by the Agent reply action)",
            delivery.JobKey,
            delivery.ConnectionId);
    }

    private static bool ShouldRenderRetry(string projectId, SlackTerminalDelivery delivery) =>
        !string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
        && string.Equals(delivery.Status, "failed", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(delivery.SessionId)
        && !string.IsNullOrWhiteSpace(delivery.TurnId)
        && AgentSessionRetryPolicy.IsRetryable(delivery.FailureCategory);

    private static string FailureNoticeText(SlackTerminalDelivery delivery) =>
        string.IsNullOrWhiteSpace(delivery.FailureReason)
            ? "The Agent run failed."
            : $"The Agent run failed: {SlackSecretRedactor.Redact(delivery.FailureReason)}";

}

public sealed record SlackTerminalDelivery(
    string JobKey,
    string WorkLabel,
    string ConnectionId,
    string WorkspaceTeamId,
    string ConversationId,
    string Status,
    string? Message,
    string? FailureReason,
    string? FailureCategory,
    int ArtifactCount,
    int? ExitCode,
    string? ThreadTs = null,
    string? MessageTs = null,
    string? SessionId = null,
    string? TurnId = null,
    string? SlackUserId = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(JobKey)
            || string.IsNullOrWhiteSpace(WorkLabel)
            || string.IsNullOrWhiteSpace(ConnectionId)
            || string.IsNullOrWhiteSpace(WorkspaceTeamId)
            || string.IsNullOrWhiteSpace(ConversationId)
            || Status is not ("completed" or "failed" or "cancelled" or "unknown"))
        {
            throw new InvalidOperationException("Terminal delivery event has invalid routing or status facts.");
        }
    }

    public string ResolveProjectId(IReadOnlyDictionary<string, string> extensions) =>
        extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var projectId)
        && !string.IsNullOrWhiteSpace(projectId)
            ? projectId
             : throw new InvalidOperationException("Terminal delivery event has no project lineage.");
}

[Subscription(
    Type = EventCatalog.ReverseDns.AgentSessionFollowupDelivery,
    Identity = "Mohist.Server.Infrastructure.Slack.SlackFollowupDeliveryHandler")]
public sealed class SlackFollowupDeliveryHandler : ICloudEventHandler
{
    private readonly SlackTerminalDeliveryHandler _inner;

    public SlackFollowupDeliveryHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<SlackTerminalDeliveryHandler> log)
    {
        _inner = new SlackTerminalDeliveryHandler(scopeFactory, log);
    }

    public bool Filter(CloudEvent evt) => _inner.Filter(evt);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => _inner.HandleAsync(evt, ct);
}
