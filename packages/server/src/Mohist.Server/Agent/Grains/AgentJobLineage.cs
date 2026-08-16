using System.Text.Json;
using System.Text.RegularExpressions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Pure helper that builds the CloudEvent envelope for AgentJob terminal
/// failures. Every executable AgentJob emits
/// <c>com.mohist.agent.job.failed</c>. Lineage is stamped from the durable
/// launch context (<see cref="AgentJobInput"/> +
/// <see cref="RoutedAgentLaunchPlan"/>) so the failure event never re-reads
/// mutable Agent / Issue / Workflow state.
///
/// <para>
/// <c>agentid</c> is required by the event contract (per
/// <see cref="EventProducerFamily.AgentJob"/> conformance). Issue / epic /
/// workflow-run lineage are stamped when the launch context carries it;
/// their absence is valid for jobs without that context. The grain's
/// persistence story is unchanged:
/// the emission flows through the no-DbContext
/// <see cref="Mohist.Server.Infrastructure.Events.IEventStore.AppendAsync(CloudEvent,System.Threading.CancellationToken)"/>
/// overload so the durable write does not block on the AgentJob's own
/// grain-storage state transaction.
/// </para>
/// </summary>
public static class AgentJobLineage
{
    private const int SummaryFactMaxLength = 480;
    private const int AssistantTextMaxLength = 4_000;
    private const int WorkLabelMaxLength = 80;
    private static readonly Regex SecretAssignment = new(
        "(?i)(?:\\\"(?:token|secret|api[_-]?key|password)[^\\\"]*\\\"\\s*:\\s*\\\"|(?:token|secret|api[_-]?key|password)\\s*[:=]\\s*)(?:[^\\\"\\s,}]+|[^\\\"]*\\\")",
        RegexOptions.Compiled);
    private static readonly Regex SlackToken = new("xox[baprs]-[A-Za-z0-9-]+", RegexOptions.Compiled);

    public sealed record FailurePayload(
        string JobKey,
        AgentJobStatus Status,
        string? FailureReason,
        string? FailureCategory,
        string? ProjectId,
        string? AgentId);

    public static IReadOnlyDictionary<string, string> BuildExtensions(
        AgentJobInput? input,
        RoutedAgentLaunchPlan? routedPlan = null)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        var agentId = !string.IsNullOrWhiteSpace(input?.AgentId)
            ? input!.AgentId
            : routedPlan?.AgentId;
        if (!string.IsNullOrWhiteSpace(agentId))
            extensions[EventCatalog.Lineage.AgentId] = agentId!;
        var projectId = !string.IsNullOrWhiteSpace(input?.ProjectId)
            ? input!.ProjectId
            : routedPlan?.ProjectId;
        if (!string.IsNullOrWhiteSpace(projectId))
            extensions[EventCatalog.Lineage.ProjectId] = projectId!;
        var issueNumber = input?.IssueNumber ?? routedPlan?.IssueNumber;
        if (issueNumber is > 0)
            extensions[EventCatalog.Lineage.Issue] = issueNumber!.Value.ToString();
        var epicNumber = input?.EpicNumber ?? routedPlan?.EpicNumber;
        if (epicNumber is > 0)
            extensions[EventCatalog.Lineage.Epic] = epicNumber!.Value.ToString();
        var workflowRunId = !string.IsNullOrWhiteSpace(input?.WorkflowRunId)
            ? input!.WorkflowRunId
            : routedPlan?.WorkflowRunId;
        if (!string.IsNullOrWhiteSpace(workflowRunId))
            extensions[EventCatalog.Lineage.WorkflowRunId] = workflowRunId!;
        return extensions;
    }

    public static CloudEvent BuildFailureEnvelope(
        string jobKey,
        DateTimeOffset now,
        FailurePayload payload,
        IReadOnlyDictionary<string, string> extensions)
    {
        var source = AgentJobEventPersistence.AgentJobSource(jobKey);
        var data = JsonSerializer.SerializeToElement(new
        {
            jobKey = payload.JobKey,
            status = payload.Status.ToString().ToLowerInvariant(),
            failureReason = payload.FailureReason,
            failureCategory = payload.FailureCategory,
            projectId = payload.ProjectId,
            agentId = payload.AgentId,
        }, JSON.Options);
        return new CloudEvent(
            id: AgentJobSessionDeliveryIds.FailureEventId(jobKey),
            source: new Uri(source, UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobFailed,
            time: now,
            data: data,
            subject: jobKey,
            extensions: extensions);
    }

    public static CloudEvent BuildTerminalDeliveryEnvelope(
        string jobKey,
        PendingTerminalDeliveryEvent payload,
        IReadOnlyDictionary<string, string> extensions,
        string? sessionLaunchPrompt)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            jobKey,
            workLabel = BuildWorkLabel(sessionLaunchPrompt),
            connectionId = payload.Origin.ConnectionId,
            workspaceTeamId = payload.Origin.WorkspaceTeamId,
            slackUserId = payload.Origin.SlackUserId,
            conversationId = payload.Origin.ConversationId,
            threadTs = payload.Origin.ThreadTs,
            messageTs = payload.Origin.MessageTs,
            status = payload.Status.ToString().ToLowerInvariant(),
            message = SafeSummaryFact(payload.Message),
            failureReason = SafeSummaryFact(payload.FailureReason),
            failureCategory = SafeSummaryFact(payload.FailureCategory),
            artifactCount = payload.ArtifactCount,
            exitCode = payload.ExitCode,
            assistantText = ExtractAssistantText(payload.Output),
        }, JSON.Options);
        return new CloudEvent(
            id: payload.EventId,
            source: new Uri(AgentJobEventPersistence.AgentJobSource(jobKey), UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobTerminalDelivery,
            time: payload.RecordedAt,
            data: data,
            subject: jobKey,
            extensions: extensions);
    }

    public static CloudEvent BuildSubagentTerminalEnvelope(
        string jobKey,
        PendingSubagentTerminalEvent payload)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            childLaunchJobId = payload.Origin.ChildLaunchJobId,
            childSessionId = payload.Origin.ChildSessionId,
            parentSessionId = payload.Origin.ParentSessionId,
            parentAgentId = payload.Origin.ParentAgentId,
            edgeId = payload.Origin.EdgeId,
            initialTurnId = payload.Origin.InitialTurnId,
            status = payload.Status.ToString().ToLowerInvariant(),
            resultReference = payload.ResultReference,
        }, JSON.Options);
        return new CloudEvent(
            id: payload.EventId,
            source: new Uri(AgentJobEventPersistence.AgentJobSource(jobKey), UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobSubagentTerminal,
            time: payload.RecordedAt,
            data: data,
            subject: jobKey);
    }

    /// <summary>
    /// Envelope for the workflow-originated terminal delivery
    /// (<c>com.mohist.agent.job.workflow-terminal</c>). The payload is
    /// fully typed from the frozen obligation: the invocation identity
    /// (invocation / project / workflow run / task run / work id plus the
    /// minted job / session / input / turn ids), the terminal facts
    /// (status, output, failure reason / category, exit code, artifact
    /// upload ids), the boundary completion evaluation, and the recorded
    /// timestamp. The stable event id (<c>workflow-terminal:{jobKey}</c>)
    /// makes duplicate or retried appends resolve against the same event
    /// identity. The Agent facts never ride the Workflow task-report
    /// endpoint — this event is the Agent-to-Workflow transport channel.
    /// </summary>
    public static CloudEvent BuildWorkflowTerminalEnvelope(
        string jobKey,
        PendingWorkflowTerminalDelivery payload,
        IReadOnlyDictionary<string, string> extensions)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            invocationId = payload.InvocationId,
            projectId = payload.ProjectId,
            workflowRunId = payload.WorkflowRunId,
            taskRunId = payload.TaskRunId,
            workId = payload.WorkId,
            jobId = payload.JobId,
            sessionId = payload.SessionId,
            inputId = payload.InputId,
            turnId = payload.TurnId,
            status = payload.Status.ToString().ToLowerInvariant(),
            output = ParseOutputElement(payload.Output),
            message = payload.Message,
            failureReason = payload.FailureReason,
            failureCategory = payload.FailureCategory,
            exitCode = payload.ExitCode,
            artifactUploadIds = payload.ArtifactUploadIds,
            evaluation = SerializeEvaluation(payload.Evaluation),
            recordedAt = payload.RecordedAt,
        }, JSON.Options);
        return new CloudEvent(
            id: payload.EventId,
            source: new Uri(AgentJobEventPersistence.AgentJobSource(jobKey), UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobWorkflowTerminal,
            time: payload.RecordedAt,
            data: data,
            subject: jobKey,
            extensions: extensions);
    }

    private static JsonElement? ParseOutputElement(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        try
        {
            var element = JsonDocument.Parse(output).RootElement.Clone();
            return element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? element
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? SerializeEvaluation(AgentJobCompletionEvaluation? evaluation) =>
        evaluation is null
            ? null
            : new
            {
                satisfied = evaluation.Satisfied,
                matched = evaluation.Matched,
                missingFiles = evaluation.MissingFiles.Select(path => new { path }).ToArray(),
                missingMarkers = evaluation.MissingMarkers.Select(miss => new
                {
                    path = miss.Path,
                    contains = miss.Contains,
                }).ToArray(),
                failIfMatches = evaluation.FailIfMatches.Select(match => new
                {
                    marker = match.Marker,
                    failIf = match.FailIf,
                    path = match.Path,
                }).ToArray(),
                message = evaluation.Message,
            };

    public static string? ExtractAssistantText(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text.GetString()))
            {
                return null;
            }

            var redacted = SlackToken.Replace(SecretAssignment.Replace(text.GetString()!, "***"), "***");
            return redacted.Length <= AssistantTextMaxLength
                ? redacted
                : redacted[..AssistantTextMaxLength];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildWorkLabel(string? sessionLaunchPrompt)
    {
        var label = SafeSummaryFact(sessionLaunchPrompt);
        if (string.IsNullOrWhiteSpace(label))
            return "Unknown task";

        return label.Length <= WorkLabelMaxLength
            ? label
            : label[..WorkLabelMaxLength];
    }

    private static string? SafeSummaryFact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        normalized = SecretAssignment.Replace(normalized, "***");
        normalized = SlackToken.Replace(normalized, "***");
        return normalized.Length <= SummaryFactMaxLength ? normalized : normalized[..SummaryFactMaxLength];
    }
}
