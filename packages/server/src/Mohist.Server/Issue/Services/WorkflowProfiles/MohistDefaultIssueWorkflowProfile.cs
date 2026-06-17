using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistDefaultIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public MohistDefaultIssueWorkflowProfile(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
        : base(promptLoader, dbFactory)
    {
    }

    public override string Id => IssueWorkflowProfiles.DefaultId;
    public override string DisplayName => "Mohist Default";
    public override string Description => ResolveDescription();
    public override bool IsDefault => true;
    public override IReadOnlyList<string> SuitableFor { get; } = [
        "feature development involving UI or backend changes",
        "bug fixes requiring a full plan-build-check-integrate lifecycle",
        "OpenSpec-driven workflows with structured change artifacts",
        "issues needing approval gates between stages"
    ];

    private static string ResolveDescription()
    {
        var description = MohistWorkflow.Definition.Description;
        return string.IsNullOrWhiteSpace(description) ? "No description provided" : description;
    }

    public MohistDefaultWorkflowState ProjectWorkflowState(Domain.Issue issue, WorkflowStatusView? workflow) =>
        MohistDefaultWorkflowProjection.ProjectWorkflowState(
            issue.Number,
            issue.Title,
            issue.Status,
            workflow);

    public MohistDefaultWorkflowState ProjectWorkflowState(IssueReadModel issue, WorkflowStatusView? workflow) =>
        MohistDefaultWorkflowProjection.ProjectWorkflowState(
            issue.Number,
            issue.Title,
            issue.Status,
            workflow);
}
