using System.Text.Json;
using Mohist.Server.Infrastructure;
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
                ["agent"] = JSON.SerializeToElement(agentConfig),
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
            : JSON.DeserializeElement("{}");

        if (prompts is not null)
            root["prompts"] = JSON.SerializeToElement(prompts);

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

    /// <summary>
    /// Builds a <see cref="VariableBundle"/> patch that writes the user-supplied
    /// model metadata into the issue profile's Variables JSON. Used by PATCH
    /// (and create) to persist <c>model</c>, <c>agentConfig</c>,
    /// <c>stageModels</c>, and <c>stageModelVariants</c> on the workflow profile
    /// path. The resulting bundle is fed to
    /// <see cref="Workflow.Services.IssueWorkflowProfileManager.PatchVariablesAsync"/>,
    /// so each provided key overlays the existing variables via deep merge —
    /// absent keys stay untouched.
    /// </summary>
    /// <remarks>
    /// <para>Storage layout (matches <c>ApplyIssueWorkflowVariables</c> read path):</para>
    /// <list type="bullet">
    ///   <item><c>model</c>                -> <c>vars.agent.model</c></item>
    ///   <item><c>agentConfig</c>          -> <c>vars.agent</c> (deep merge)</item>
    ///   <item><c>stageModels</c>          -> <c>stages.&lt;stage&gt;.vars.agent.model</c></item>
    ///   <item><c>stageModelVariants</c>   -> <c>stages.&lt;stage&gt;.vars.agent.variant</c></item>
    /// </list>
    /// </remarks>
    public static VariableBundle BuildModelMetadataPatch(
        string? model,
        Dictionary<string, object?>? agentConfig,
        Dictionary<string, string>? stageModels,
        Dictionary<string, string>? stageModelVariants)
    {
        var hasRoot = model is not null || agentConfig is not null;
        var hasStages = stageModels is not null || stageModelVariants is not null;
        if (!hasRoot && !hasStages) return VariableBundle.Empty;

        var rootVars = hasRoot ? new Dictionary<string, JsonElement?>(StringComparer.Ordinal) : null;
        if (rootVars is not null)
        {
            // Read-back path expects vars.agent.model, not vars.model — so the
            // "model" key is merged into the same agent object as agentConfig.
            var rootAgent = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (agentConfig is not null)
            {
                foreach (var (k, v) in agentConfig)
                    rootAgent[k] = v;
            }
            if (model is not null)
                rootAgent["model"] = model;

            if (rootAgent.Count > 0)
                rootVars["agent"] = JsonSerializer.SerializeToElement(rootAgent, WorkflowVariableJson.Options);
        }

        Dictionary<string, StageVariables>? stages = null;
        if (hasStages)
        {
            stages = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);
            if (stageModels is not null)
            {
                foreach (var (stage, stageModel) in stageModels)
                {
                    if (string.IsNullOrWhiteSpace(stage)) continue;
                    var stageDict = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["agent"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["model"] = stageModel,
                        },
                    };
                    stages[stage] = new StageVariables(JsonSerializer.SerializeToElement(stageDict, WorkflowVariableJson.Options));
                }
            }
            if (stageModelVariants is not null)
            {
                foreach (var (stage, variant) in stageModelVariants)
                {
                    if (string.IsNullOrWhiteSpace(stage)) continue;
                    var stageAgent = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var existing = stages.TryGetValue(stage, out var existingStage) && existingStage.Vars.HasValue
                        ? TryReadAgentFromStage(existingStage.Vars.Value)
                        : null;
                    if (existing is not null)
                    {
                        foreach (var (k, v) in existing) stageAgent[k] = v;
                    }
                    stageAgent["variant"] = variant;

                    var stageDict = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["agent"] = stageAgent,
                    };
                    stages[stage] = new StageVariables(JsonSerializer.SerializeToElement(stageDict, WorkflowVariableJson.Options));
                }
            }
        }

        var rootElement = rootVars is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(rootVars, WorkflowVariableJson.Options);
        return new VariableBundle(rootElement, stages);
    }

    private static StageVariables GetOrCreateStageVars(Dictionary<string, StageVariables> stages, string stage)
    {
        if (stages.TryGetValue(stage, out var existing)) return existing;
        return new StageVariables(null);
    }

    private static Dictionary<string, object?> ReadStageVarsDict(StageVariables stageVars)
    {
        if (!stageVars.Vars.HasValue || stageVars.Vars.Value.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            stageVars.Vars.Value.GetRawText(),
            WorkflowVariableJson.Options);
        return dict is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(dict, StringComparer.Ordinal);
    }

    private static Dictionary<string, object?>? TryReadAgentFromStage(JsonElement stageVarsElement)
    {
        if (stageVarsElement.ValueKind != JsonValueKind.Object) return null;
        if (!stageVarsElement.TryGetProperty("agent", out var agentElement) || agentElement.ValueKind != JsonValueKind.Object)
            return null;
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(
            agentElement.GetRawText(),
            WorkflowVariableJson.Options);
    }

    private static VariableBundle BuildBuiltInContext(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace)
    {
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JSON.SerializeToElement(
                new { system = "mohist", runId = workflowRunId }),
            ["issue"] = JSON.SerializeToElement(
                new
                {
                    projectId = issue.ProjectId,
                    number = issue.Number,
                    title = issue.Title,
                    body = issue.Body ?? string.Empty,
                }),
            ["project"] = JSON.SerializeToElement(
                new
                {
                    id = project.Id,
                    name = project.Name,
                }),
            ["repository"] = JSON.SerializeToElement(
                new
                {
                    name = project.RepositoryName,
                    gitUrl = project.RepositoryGitUrl,
                    baseBranch = project.RepositoryBaseBranch,
                }),
            ["openspecChangeName"] = JSON.SerializeToElement(
                MohistDefaultWorkflowProjection.ChangeName(issue.Number)),
            ["openspecChangeDir"] = JSON.SerializeToElement(
                MohistDefaultWorkflowProjection.ChangeDir(issue.Number)),
            ["workspace"] = JSON.SerializeToElement(
                new
                {
                    path = workspace.Path,
                    branch = workspace.Branch,
                    changeDir = workspace.ChangeDir,
                }),
        };

        var varsJson = JSON.Serialize(variables);
        var varsElement = JSON.DeserializeElement(varsJson);
        return new VariableBundle(varsElement);
    }

    private static VariableBundle FromRoot(Dictionary<string, JsonElement?> variables)
    {
        var varsJson = JSON.Serialize(variables);
        var varsElement = JSON.DeserializeElement(varsJson);
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
