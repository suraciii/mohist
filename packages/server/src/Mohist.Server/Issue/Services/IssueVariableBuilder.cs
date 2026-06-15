using System.Text.Json;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using MohistIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Builds workflow variables for issue execution. Layers, lowest priority
/// first, are merged via <see cref="VariableBundle.MergeAll"/>:
///
///   1. Project variables (<c>ProjectWorkflowProfile.Variables</c>) — defaults.
///   2. Issue variables (<c>IssueWorkflowProfile.Variables</c>) — overrides.
///   3. Runtime context (<c>mohist</c>, <c>issue</c>, <c>project</c>,
///      <c>repository</c>, <c>workspace</c>, <c>openspecChangeDir</c>) —
///      authoritative dispatch context.
///
/// Anything a user patches onto the project (e.g. <c>vars.agent.model</c>) is
/// therefore visible to the dispatch layer unless the issue overrides it. This
/// is the single fix for the issue-#80 dispatch stall where the build agent
/// silently fell through to opencode's local default because <c>vars.agent</c>
/// was never populated.
///
/// The built-in context is deliberately composed here so profile preview,
/// issue start, and dispatch rendering cannot drift.
/// </summary>
public static class IssueVariableBuilder
{
    public static VariableBundle Build(
        VariableBundle? projectBundle,
        VariableBundle? issueBundle,
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace)
    {
        return VariableBundle.MergeAll(projectBundle, issueBundle, BuildRuntimeContext(workflowRunId, issue, project, workspace));
    }

    public static VariableBundle Build(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace,
        Dictionary<string, object?>? agentConfig = null)
    {
        var userDefaults = agentConfig is null
            ? VariableBundle.Empty
            : FromRoot(new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
            {
                ["agent"] = JsonSerializer.SerializeToElement(agentConfig, WorkflowVariableJson.Options),
            });

        return Build(userDefaults, VariableBundle.Empty, workflowRunId, issue, project, workspace);
    }

    public static Dictionary<string, JsonElement?> BuildRootVariables(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace,
        Dictionary<string, object?>? agentConfig = null,
        IReadOnlyDictionary<string, string>? prompts = null)
    {
        var bundle = Build(workflowRunId, issue, project, workspace, agentConfig);
        var root = ToRootDictionary(bundle);
        root["vars"] = bundle.Vars.HasValue && bundle.Vars.Value.ValueKind == JsonValueKind.Object
            ? bundle.Vars.Value.Clone()
            : JsonSerializer.Deserialize<JsonElement>("{}");

        if (prompts is not null)
            root["prompts"] = JsonSerializer.SerializeToElement(prompts, WorkflowVariableJson.Options);

        return root;
    }

    public static WorkspaceIdentity BuildWorkspaceIdentity(string workflowRunId, MohistIssue issue, WorkflowProjectContext project, string runnerRoot)
    {
        var changeDir = MohistDefaultWorkflowProjection.ChangeDir(issue.Number);
        return new WorkspaceIdentity(
            Path: Mohist.Server.Infrastructure.Workspace.MohistWorkspaceLayout.IssueWorkspacePath(runnerRoot, project.Name, issue.Number),
            Branch: WorkflowRunBranch.For(workflowRunId),
            ChangeDir: changeDir);
    }

    private static VariableBundle BuildRuntimeContext(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace)
    {
        return FromRoot(new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number }, WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(new { id = project.Id, name = project.Name }, WorkflowVariableJson.Options),
            ["repository"] = JsonSerializer.SerializeToElement(new { name = project.RepositoryName, gitUrl = project.RepositoryGitUrl, baseBranch = project.RepositoryBaseBranch }, WorkflowVariableJson.Options),
            ["workspace"] = JsonSerializer.SerializeToElement(new { path = workspace.Path, branch = workspace.Branch, changeDir = workspace.ChangeDir }, WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeDir(issue.Number), WorkflowVariableJson.Options),
        });
    }

    private static VariableBundle FromRoot(Dictionary<string, JsonElement?> variables)
    {
        var varsJson = JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
        var varsElement = JsonSerializer.Deserialize<JsonElement>(varsJson);
        return new VariableBundle(varsElement);
    }

    private static Dictionary<string, JsonElement?> ToRootDictionary(VariableBundle bundle)
    {
        var result = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (bundle.Vars is not { ValueKind: JsonValueKind.Object } vars) return result;

        foreach (var property in vars.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }
}
