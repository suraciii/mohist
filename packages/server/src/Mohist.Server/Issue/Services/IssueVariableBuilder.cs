using System.Text.Json;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using MohistIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Builds the variable bundle persisted on <c>IssueWorkflowProfile</c> at issue
/// start (T1). The issue profile is the single resolution point: project values
/// win, global <c>config.jsonc</c> values fill gaps, and the built-in calling
/// context (<c>mohist</c> / <c>issue</c> / <c>project</c> / <c>repository</c> /
/// <c>openspec*</c>) is layered on top. The result is snapshotted once, at
/// issue creation, so subsequent edits to project or global <c>Variables</c>
/// do not retroactively change this issue's effective variables.
///
/// Merge layers, lowest priority first, are merged via
/// <see cref="VariableBundle.MergeAll"/>:
///
///   1. Global <see cref="Mohist.Server.Workflow.Domain.VariableBundle"/> from
///      <c>config.jsonc</c> (exposed by <c>ConfigService.GetVariables()</c>).
///   2. Project <c>VariableBundle</c> (<c>ProjectWorkflowProfile.Variables</c>).
///   3. Built-in calling context.
///
/// The merge is symmetric: <c>vars</c> and each
/// <c>stages.&lt;stage&gt;.vars</c> use the same project-over-global precedence
/// via <see cref="VariableBundle.MergeAll"/>, with no special-cased key.
/// </summary>
public static class IssueVariableBuilder
{
    public static VariableBundle Build(
        VariableBundle? globalBundle,
        VariableBundle? projectBundle,
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project)
    {
        var builtIn = BuildBuiltInContext(workflowRunId, issue, project);
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

        return Build(userDefaults, VariableBundle.Empty, workflowRunId, issue, project);
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
                },
                WorkflowVariableJson.Options),
            ["repository"] = JsonSerializer.SerializeToElement(
                new
                {
                    name = project.RepositoryName,
                    baseBranch = project.RepositoryBaseBranch,
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
