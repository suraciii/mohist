using System.Text.Json;
using System.Text.RegularExpressions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Services;

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
    private const int WorkLabelMaxLength = 80;
    private static readonly Regex SecretAssignment = new(
        "(?i)(?:\\\"(?:token|secret|api[_-]?key|password)[^\\\"]*\\\"\\s*:\\s*\\\"|(?:token|secret|api[_-]?key|password)\\s*[:=]\\s*)(?:[^\\\"\\s,}]+|[^\\\"]*\\\")",
        RegexOptions.Compiled);

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
            failureReason = payload.FailureReason is { } reason
                ? SlackSecretRedactor.Redact(reason)
                : null,
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
        string? sessionLaunchPrompt,
        string? sessionId = null,
        string? turnId = null)
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
            sessionId,
            turnId,
            status = payload.Status.ToString().ToLowerInvariant(),
            message = SafeSummaryFact(payload.Message),
            failureReason = SafeSummaryFact(payload.FailureReason),
            failureCategory = SafeSummaryFact(payload.FailureCategory),
            artifactCount = payload.ArtifactCount,
            exitCode = payload.ExitCode,
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

    public static CloudEvent BuildWorkflowTerminalEnvelope(
        string jobKey,
        PendingWorkflowAgentTerminalEvent payload,
        IReadOnlyDictionary<string, string> extensions)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            jobKey,
            invocationId = payload.Origin.InvocationId,
            commandId = payload.Origin.CommandId,
            workflowRunId = payload.Origin.WorkflowRunId,
            actionAttemptId = payload.Origin.ActionAttemptId,
            workId = payload.Origin.WorkId,
            stage = payload.Origin.Stage,
            requestFingerprint = payload.Origin.RequestFingerprint,
            status = payload.Status.ToString().ToLowerInvariant(),
            message = payload.Message,
            output = payload.Output,
            artifactUploadIds = payload.ArtifactUploadIds,
            failureReason = payload.FailureReason,
            failureCategory = payload.FailureCategory,
            exitCode = payload.ExitCode,
            resultFingerprint = payload.ResultFingerprint,
            agentSessionId = payload.AgentSessionId,
            initialInputId = payload.InitialInputId,
            initialTurnId = payload.InitialTurnId,
            addTasksJson = payload.AddTasksJson,
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
        normalized = SlackSecretRedactor.Redact(normalized, "***");
        return normalized.Length <= SummaryFactMaxLength ? normalized : normalized[..SummaryFactMaxLength];
    }
}
