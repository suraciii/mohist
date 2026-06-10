namespace Mohist.Server.Infrastructure.Data.Events;

internal static class WorkflowRunEventPersistence
{
    // CloudEvents 1.0.2 source URI-reference. Format: /{context}/{aggregate}/{id}.
    public const string SourcePrefix = "/mohist/workflow-runs/";
    public static string WorkflowRunSource(string workflowRunId) => $"{SourcePrefix}{workflowRunId}";
}
