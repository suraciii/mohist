using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;

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
        var result = await outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
            delivery.ResolveProjectId(evt.Extensions),
            delivery.ConnectionId,
            delivery.WorkspaceTeamId,
            delivery.ConversationId,
            delivery.Status == "completed" ? SlackOutboxKinds.TerminalResult : SlackOutboxKinds.ExplicitFailure,
            $"agent-job:{delivery.JobKey}:terminal-delivery",
            JsonSerializer.Serialize(new { text = Render(delivery) }),
            delivery.ThreadTs), ct);

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
        var conclusion = delivery.Status switch
        {
            "completed" => "The task completed.",
            "failed" => "The task failed.",
            _ => "The task outcome is unknown.",
        };
        var evidence = BuildEvidence(delivery);
        var nextStep = delivery.Status switch
        {
            "completed" => "Review the evidence and send the next request.",
            "failed" => "Reply with corrected instructions or retry after fixing the reported problem.",
            _ => "Wait for reconciliation, then retry only after the outcome is confirmed.",
        };
        return $"Task: {delivery.WorkLabel}\nConclusion: {conclusion}\nEvidence: {evidence}\nNext step: {nextStep}";
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
    string? ThreadTs = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(JobKey)
            || string.IsNullOrWhiteSpace(WorkLabel)
            || string.IsNullOrWhiteSpace(ConnectionId)
            || string.IsNullOrWhiteSpace(WorkspaceTeamId)
            || string.IsNullOrWhiteSpace(ConversationId)
            || Status is not ("completed" or "failed" or "unknown"))
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
