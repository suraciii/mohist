using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public sealed record WorkflowAgentSessionContext(
    string ProjectId,
    string WorkflowRunId,
    string SessionName,
    int? IssueNumber = null,
    string? WorkId = null,
    string? WorkType = null,
    string? Stage = null,
    string? Title = null,
    int? EpicNumber = null);

public static class WorkflowAgentSessionMetadata
{
    public static IReadOnlyDictionary<string, string> LookupLabels(
        string projectId,
        string workflowRunId,
        string sessionName) =>
        Labels(new WorkflowAgentSessionContext(projectId, workflowRunId, sessionName));

    public static IReadOnlyDictionary<string, string> Labels(WorkflowAgentSessionContext context)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = context.ProjectId,
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = context.WorkflowRunId,
            [AgentSessionQueryMetadataKeys.SessionName] = context.SessionName,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
        };
        if (context.IssueNumber is > 0)
            labels[AgentSessionQueryMetadataKeys.IssueNumber] = context.IssueNumber.Value.ToString();
        if (context.EpicNumber is > 0)
            labels[AgentSessionQueryMetadataKeys.EpicNumber] = context.EpicNumber.Value.ToString();
        if (!string.IsNullOrWhiteSpace(context.WorkId))
            labels[AgentSessionQueryMetadataKeys.WorkId] = context.WorkId;
        if (!string.IsNullOrWhiteSpace(context.WorkType))
            labels[AgentSessionQueryMetadataKeys.WorkType] = context.WorkType;
        if (!string.IsNullOrWhiteSpace(context.Stage))
            labels[AgentSessionQueryMetadataKeys.Stage] = context.Stage;
        return labels;
    }

    public static AgentSessionMetadata Metadata(WorkflowAgentSessionContext context)
    {
        IReadOnlyDictionary<string, string>? annotations = string.IsNullOrWhiteSpace(context.Title)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.Title] = context.Title
            };
        return new AgentSessionMetadata(Labels(context), annotations);
    }
}
