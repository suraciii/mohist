using System.Text.Json;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.WorkflowProfiles;

public class MohistDefaultIssueWorkflowProfile : IIssueWorkflowProfile
{
    public string Id => IssueWorkflowProfiles.DefaultId;
    public string DisplayName => "Mohist Default";
    public string Description => "Plan, build, check, and integrate an issue using OpenSpec artifacts.";
    public bool IsDefault => true;
    public WorkflowDefinitionInput Definition => MohistPipeline.Definition;

    public string BuildVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project)
    {
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { system = "mohist", runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number, title = issue.Title, body = issue.Body ?? "" }, WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(new { id = project.Id, name = project.Name, path = project.Path, baseBranch = project.BaseBranch, defaultBranch = project.BaseBranch }, WorkflowVariableJson.Options),
            ["openspecChangeName"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeName(issue.Number), WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeDir(issue.Number), WorkflowVariableJson.Options),
            ["model"] = JsonSerializer.SerializeToElement(new { @default = issue.Model ?? "", stage = issue.StageModels ?? new Dictionary<string, string>() }, WorkflowVariableJson.Options),
            ["vars"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>(), WorkflowVariableJson.Options),
        };
        return JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
    }

    public MohistDefaultWorkflowState Project(Domain.Issue issue, WorkflowStatusSnapshot? workflow) =>
        MohistDefaultWorkflowProjection.Project(
            issue.Number,
            issue.Title,
            IssueDomainNames.Status(issue.Status),
            issue.Attention,
            issue.BlockedReason,
            workflow);

    public MohistDefaultWorkflowState Project(IssueReadModel issue, WorkflowStatusSnapshot? workflow) =>
        MohistDefaultWorkflowProjection.Project(
            issue.Number,
            issue.Title,
            issue.Stage,
            issue.Attention,
            issue.BlockedReason,
            workflow);
}
