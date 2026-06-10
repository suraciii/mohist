using System.Text.Json;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using MohistIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Builds the variable bundle persisted on <c>IssueWorkflowProfile</c> at issue
/// start. Layers, lowest priority first, are merged via
/// <see cref="VariableBundle.MergeAll"/>:
///
///   1. Built-in calling context (mohist / issue / project / repository / openspec*)
///   2. Project variables (<c>ProjectWorkflowProfile.Variables</c>) — defaults.
///   3. Issue variables (<c>IssueWorkflowProfile.Variables</c>) — overrides.
///
/// Anything a user patches onto the project (e.g. <c>vars.agent.model</c>) is
/// therefore visible to the dispatch layer unless the issue overrides it. This
/// is the single fix for the issue-#80 dispatch stall where the build agent
/// silently fell through to opencode's local default because <c>vars.agent</c>
/// was never populated.
/// </summary>
public static class IssueVariableBuilder
{
    public static VariableBundle Build(
        VariableBundle? projectBundle,
        VariableBundle? issueBundle,
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project)
    {
        var builtIn = BuildBuiltInContext(workflowRunId, issue, project);
        return VariableBundle.MergeAll(builtIn, projectBundle, issueBundle);
    }

    private static VariableBundle BuildBuiltInContext(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project)
    {
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(
                new { system = "mohist", runId = workflowRunId },
                WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(
                new
                {
                    id = issue.Id,
                    number = issue.Number,
                    title = issue.Title,
                    body = issue.Body ?? string.Empty,
                },
                WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(
                new
                {
                    id = project.Id,
                    name = project.Name,
                    path = project.Path,
                    baseBranch = project.BaseBranch,
                    defaultBranch = project.BaseBranch,
                },
                WorkflowVariableJson.Options),
            ["repository"] = JsonSerializer.SerializeToElement(
                new
                {
                    name = project.RepositoryName,
                    path = project.RepositoryPath,
                    remote = project.RepositoryRemote,
                    baseBranch = project.RepositoryBaseBranch ?? project.BaseBranch,
                },
                WorkflowVariableJson.Options),
            ["openspecChangeName"] = JsonSerializer.SerializeToElement(
                MohistDefaultWorkflowProjection.ChangeName(issue.Number),
                WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(
                MohistDefaultWorkflowProjection.ChangeDir(issue.Number),
                WorkflowVariableJson.Options),
        };

        var varsJson = JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
        var varsElement = JsonSerializer.Deserialize<JsonElement>(varsJson);
        return new VariableBundle(varsElement);
    }
}
