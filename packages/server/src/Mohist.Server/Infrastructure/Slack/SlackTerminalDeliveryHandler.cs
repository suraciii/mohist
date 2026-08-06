using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Services;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack;

[Subscription(
    Type = EventCatalog.ReverseDns.AgentJobTerminalDelivery,
    Identity = "Mohist.Server.Infrastructure.Slack.SlackTerminalDeliveryHandler")]
public sealed class SlackTerminalDeliveryHandler : ICloudEventHandler
{
    private const int MaximumEvidenceLength = 600;
    private static readonly Regex SecretAssignment = new(
        "(?i)(?:\\\"(?:token|secret|api[_-]?key|password)[^\\\"]*\\\"\\s*:\\s*\\\"|(?:token|secret|api[_-]?key|password)\\s*[:=]\\s*)(?:[^\\\"\\s,}]+|[^\\\"]*\\\")",
        RegexOptions.Compiled);
    private static readonly Regex SlackToken = new("xox[baprs]-[A-Za-z0-9-]+", RegexOptions.Compiled);
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
        var sessionId = await ResolveSessionIdAsync(scope.ServiceProvider, evt, delivery, ct);
        if (string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal))
        {
            var managerTools = scope.ServiceProvider.GetService<SlackManagerToolTurnProcessor>();
            if (!string.IsNullOrWhiteSpace(sessionId)
                && managerTools is not null
                && await managerTools.ProcessAsync(sessionId, delivery, ct))
            {
                return;
            }

            await outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
                projectId,
                delivery.ConnectionId,
                delivery.WorkspaceTeamId,
                delivery.ConversationId,
                SlackOutboxKinds.TerminalResult,
                $"agent-job:{delivery.JobKey}:terminal-delivery",
                JsonSerializer.Serialize(new SlackDeliveryPayload(
                    SlackDeliveryOperations.PostMessage,
                    Render(delivery),
                    ClientMessageId: $"agent-job:{delivery.JobKey}:terminal-delivery")),
                delivery.ThreadTs ?? delivery.MessageTs,
                SlackDeliveryOwnerKinds.Manager), ct);
            return;
        }
        var text = SlackFinalReplyRenderer.AppendStableReference(Render(delivery), delivery.JobKey, sessionId);
        var blocks = await ResolveSessionBlocksAsync(scope.ServiceProvider, projectId, sessionId, ct);
        var projection = scope.ServiceProvider.GetService<SlackStatusProjection>() ?? new SlackStatusProjection(outbox);
        var source = new SlackMessageIdentity(
            delivery.WorkspaceTeamId,
            delivery.ConversationId,
            delivery.MessageTs ?? $"terminal:{delivery.JobKey}");
        var progressDispatchRef = delivery.JobKey.StartsWith("agent-session-followup:", StringComparison.Ordinal)
            ? $"{delivery.JobKey}:progress"
            : null;
        var result = await projection.EnqueueTerminalAsync(
            projectId,
            delivery.ConnectionId,
            source,
            delivery.ThreadTs ?? delivery.MessageTs,
            delivery.Status,
            text,
            $"agent-job:{delivery.JobKey}:terminal-delivery",
            progressDispatchRef,
            blocks,
            ct);

        if (result.Suppressed)
            _log.LogInformation(
                "Suppressed Slack terminal delivery for AgentJob {JobKey} on inactive connection {ConnectionId}",
                delivery.JobKey,
                delivery.ConnectionId);
        else
            _log.LogInformation(
                "Queued Slack terminal delivery for AgentJob {JobKey} on connection {ConnectionId}",
                delivery.JobKey,
                delivery.ConnectionId);
    }

    public static string Render(SlackTerminalDelivery delivery)
    {
        if (string.Equals(delivery.Status, "completed", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(delivery.AssistantText))
        {
            return SlackFinalReplyRenderer.NeutralizeSlackControlSyntax(delivery.AssistantText.Trim());
        }

        var conclusion = delivery.Status switch
        {
            "completed" => "The task completed.",
            "failed" => "The task failed.",
            "cancelled" => "The task was cancelled.",
            _ => "The task outcome is unknown.",
        };
        var evidence = BuildEvidence(delivery);
        var nextStep = delivery.Status switch
        {
            "completed" => "Review the evidence and send the next request.",
            "failed" => "Reply with corrected instructions or retry after fixing the reported problem.",
            "cancelled" => "Send a new request when you are ready to continue.",
            _ => "Wait for reconciliation, then retry only after the outcome is confirmed.",
        };
        return $"Task: {delivery.WorkLabel}\nConclusion: {conclusion}\nEvidence: {evidence}\nNext step: {nextStep}";
    }

    private async Task<string?> ResolveSessionIdAsync(
        IServiceProvider services,
        CloudEvent evt,
        SlackTerminalDelivery delivery,
        CancellationToken ct)
    {
        if (string.Equals(evt.Type, EventCatalog.ReverseDns.AgentSessionFollowupDelivery, StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(evt.Subject) ? null : evt.Subject;

        var jobs = services.GetService<IAgentJobStore>();
        if (jobs is null)
            return null;

        try
        {
            return (await jobs.LoadLedgerAsync(delivery.JobKey, ct))?.AgentSessionId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not resolve Session ID for Slack terminal delivery {JobKey}", delivery.JobKey);
            return null;
        }
    }

    private async Task<JsonElement?> ResolveSessionBlocksAsync(
        IServiceProvider services,
        string projectId,
        string? sessionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var projects = services.GetService<ProjectQuerier>();
        var links = services.GetService<SlackWebLinkBuilder>();
        if (projects is null || links is null || !links.HasUsableExternalWebUrl)
            return null;

        try
        {
            var project = await projects.GetByIdAsync(projectId);
            return project is null ? null : links.BuildOpenSession(project.Name, sessionId)?.Blocks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not build Slack session link for Project {ProjectId}", projectId);
            return null;
        }
    }

    private static string BuildEvidence(SlackTerminalDelivery delivery)
    {
        var facts = new List<string>();
        AddFact(facts, delivery.FailureReason);
        AddFact(facts, delivery.Message);
        AddFact(facts, delivery.FailureCategory is null ? null : $"category: {delivery.FailureCategory}");
        if (delivery.ExitCode is not null)
            facts.Add($"exit code: {delivery.ExitCode}");
        if (delivery.ArtifactCount > 0)
            facts.Add($"artifacts: {delivery.ArtifactCount}");
        if (facts.Count == 0)
            return "No additional result details were reported.";

        var evidence = string.Join("; ", facts);
        return evidence.Length <= MaximumEvidenceLength ? evidence : evidence[..MaximumEvidenceLength];
    }

    private static void AddFact(List<string> facts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var redacted = SlackToken.Replace(SecretAssignment.Replace(value, "***"), "***");
            facts.Add(redacted.Length <= 480 ? redacted : redacted[..480]);
        }
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
