using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Pure helper that builds the CloudEvent envelope for AgentJob terminal
/// failures (issue-491 design D2). A resolved Agent emits
/// <c>com.mohist.agent.job.failed</c>; a raw prompt job emits the distinct
/// raw-job contract because it has no Agent identity to stamp. Lineage is
/// stamped from the durable launch context (<see cref="AgentJobInput"/> +
/// <see cref="RoutedAgentLaunchPlan"/>) so the failure event never re-reads
/// mutable Agent / Issue / Workflow state.
///
/// <para>
/// <c>agentid</c> is required only by the resolved-Agent event contract (per
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
        var type = string.IsNullOrWhiteSpace(payload.AgentId)
            ? EventCatalog.ReverseDns.AgentJobRawFailed
            : EventCatalog.ReverseDns.AgentJobFailed;
        return new CloudEvent(
            id: AgentJobSessionDeliveryIds.FailureEventId(jobKey),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: now,
            data: data,
            subject: jobKey,
            extensions: extensions);
    }
}
