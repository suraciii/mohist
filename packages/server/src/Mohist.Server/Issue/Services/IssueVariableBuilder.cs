using System.Text.Json;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using MohistIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Builds the built-in calling context bundle persisted on
/// <c>IssueWorkflowProfile</c> at issue start (T1).
///
/// The issue profile stores ONLY the issue's static identity context
/// (<c>mohist</c> / <c>issue</c> / <c>project</c> / <c>repository</c> /
/// <c>openspec*</c> / <c>workspace</c>) plus any explicit overrides the user
/// sets on the issue page. It deliberately does NOT bake in global
/// (<c>config.jsonc</c>) or project <c>Variables</c>: those layers are merged
/// live at resolution time (dispatch + display) so that subsequent edits to
/// project or global <c>Variables</c> propagate to already-created issues.
/// Resolution order is <c>MergeAll(global, project, issue)</c>.
/// </summary>
public static class IssueVariableBuilder
{
    /// <summary>
    /// Builds the T1 issue-context bundle: only the built-in calling context,
    /// no global/project user variables. This is what gets persisted on the
    /// issue profile at start.
    /// </summary>
    public static VariableBundle BuildContextBundle(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace)
        => BuildBuiltInContext(workflowRunId, issue, project, workspace);

    public static VariableBundle Build(
        VariableBundle? globalBundle,
        VariableBundle? projectBundle,
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace)
    {
        var builtIn = BuildBuiltInContext(workflowRunId, issue, project, workspace);
        return VariableBundle.MergeAll(globalBundle, projectBundle, builtIn);
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

    private static VariableBundle BuildBuiltInContext(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace)
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
                },
                WorkflowVariableJson.Options),
            ["repository"] = JsonSerializer.SerializeToElement(
                new
                {
                    name = project.RepositoryName,
                    gitUrl = project.RepositoryGitUrl,
                    baseBranch = project.RepositoryBaseBranch,
                },
                WorkflowVariableJson.Options),
            ["openspecChangeName"] = JsonSerializer.SerializeToElement(
                MohistDefaultWorkflowProjection.ChangeName(issue.Number),
                WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(
                MohistDefaultWorkflowProjection.ChangeDir(issue.Number),
                WorkflowVariableJson.Options),
            ["workspace"] = JsonSerializer.SerializeToElement(
                new
                {
                    path = workspace.Path,
                    branch = workspace.Branch,
                    changeDir = workspace.ChangeDir,
                },
                WorkflowVariableJson.Options),
        };

        var varsJson = JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
        var varsElement = JsonSerializer.Deserialize<JsonElement>(varsJson);
        return new VariableBundle(varsElement);
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
