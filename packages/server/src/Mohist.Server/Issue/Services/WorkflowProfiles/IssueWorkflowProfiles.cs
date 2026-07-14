using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public static class IssueWorkflowProfiles
{
    public const string LocalId = WorkflowProfileCatalog.LocalId;
    public const string GithubPrId = WorkflowProfileCatalog.GithubPrId;
    public static readonly StringComparer IdComparer = WorkflowProfileCatalog.IdComparer;
}
