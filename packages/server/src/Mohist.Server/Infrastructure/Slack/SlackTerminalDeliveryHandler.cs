using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;

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
        await projection.FinalizeLivenessAsync(
            projectId,
            delivery.ConnectionId,
            source,
            delivery.ThreadTs ?? delivery.MessageTs,
            delivery.Status,
            progressDispatchRef,
            ct);

        _log.LogInformation(
            "Finalized Slack liveness for AgentJob {JobKey} on connection {ConnectionId} (reply body is owned by the Agent reply action)",
            delivery.JobKey,
            delivery.ConnectionId);
    }

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
    string? SlackUserId = null,
    string? AssistantText = null)
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
