using System.Text.Json;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.WorkflowProfiles;

public class MohistDefaultIssueWorkflowProfile : IIssueWorkflowProfile
{
    public string Id => IssueWorkflowProfiles.DefaultId;
    public WorkflowDefinitionInput Definition => MohistPipeline.Definition;

    public string BuildVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project)
    {
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { system = "mohist", runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number, title = issue.Title, body = issue.Body ?? "" }, WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(new { id = project.Id, name = project.Name, path = project.Path, baseBranch = project.BaseBranch, defaultBranch = project.BaseBranch }, WorkflowVariableJson.Options),
            ["artifacts"] = JsonSerializer.SerializeToElement(new { changeDir = MohistDefaultWorkflowProjection.ChangeDir(issue.Number, issue.Title) }, WorkflowVariableJson.Options),
            ["model"] = JsonSerializer.SerializeToElement(new { @default = issue.Model ?? "", stage = issue.StageModels ?? new Dictionary<string, string>() }, WorkflowVariableJson.Options),
            ["vars"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>(), WorkflowVariableJson.Options),
        };
        return JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
    }

    public MohistDefaultWorkflowState Project(Domain.Issue issue, WorkflowStatusSnapshot? workflow) =>
        MohistDefaultWorkflowProjection.Project(
            issue.Number,
            issue.Title,
            issue.Stage.ToString().ToLower(),
            issue.RuntimeStatus.ToString().ToLower(),
            issue.BlockedReason,
            workflow);

    public MohistDefaultWorkflowState Project(IssueReadModel issue, WorkflowStatusSnapshot? workflow) =>
        MohistDefaultWorkflowProjection.Project(
            issue.Number,
            issue.Title,
            issue.Stage,
            issue.Status,
            issue.BlockedReason,
            workflow);
}
