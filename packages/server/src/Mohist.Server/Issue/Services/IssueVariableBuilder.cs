using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using MohistIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Builds the variable bundle persisted on <c>IssueWorkflowProfile</c> at
/// issue start (T1).
///
/// The issue profile stores ONLY the issue's static identity context
/// (<c>mohist</c> / <c>issue</c> / <c>project</c> / <c>repository</c> /
/// <c>openspec*</c> / <c>workspace</c>) plus any explicit overrides the user
/// sets on the issue page. It deliberately does NOT bake in global
/// (<c>config.jsonc</c>) or project <c>Variables</c>: those layers are merged
/// live at resolution time (dispatch + display) so that subsequent edits to
/// project or global <c>Variables</c> propagate to already-created issues.
/// Runtime context is assembled separately when a task is dispatched.
/// </summary>
public static class IssueVariableBuilder
{
    /// <summary>
    /// Builds the T1 issue variable bundle without global/project user
    /// variables. This is what gets persisted on the issue profile at start.
    /// </summary>
    /// <remarks>
    /// when the issue's existing <see cref="VariableBundle"/>
    /// does not carry a <c>vars.agent</c> entry, the bundle seeds an empty
    /// object so built-in workflows that template-bind
    /// <c>options: ${{ vars.agent }}</c> still resolve to a usable surface.
    /// Explicit issue values (including <c>agent</c> shapes chosen by the
    /// user before start) are preserved verbatim — the seed only fires when
    /// the issue would otherwise expose <c>vars.agent</c> as undefined.
    /// </remarks>
    public static VariableBundle BuildContextBundle(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace,
        VariableBundle? existingIssueBundle = null)
    {
        var builtIn = BuildBuiltInContext(workflowRunId, issue, project, workspace);
        if (HasAgentKey(existingIssueBundle?.Vars) || HasAgentKey(builtIn.Vars))
        {
            return builtIn;
        }
        return WithEmptyAgent(builtIn);
    }

    private static bool HasAgentKey(JsonElement? vars)
    {
        return vars is { ValueKind: JsonValueKind.Object }
            && vars.Value.TryGetProperty("agent", out _);
    }

    private static VariableBundle WithEmptyAgent(VariableBundle source)
    {
        if (source.Vars is not { ValueKind: JsonValueKind.Object } vars)
        {
            var emptyRoot = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["agent"] = JsonSerializer.SerializeToElement(
                        new Dictionary<string, object?>(StringComparer.Ordinal),
                        WorkflowVariableJson.Options),
                },
                WorkflowVariableJson.Options);
            return new VariableBundle(emptyRoot, source.Stages);
        }

        var dict = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var property in vars.EnumerateObject())
        {
            dict[property.Name] = property.Value.Clone();
        }
        dict["agent"] = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(StringComparer.Ordinal),
            WorkflowVariableJson.Options);
        return new VariableBundle(
            JsonSerializer.SerializeToElement(dict, WorkflowVariableJson.Options),
            source.Stages);
    }

    public static VariableBundle Build(
        VariableBundle? globalBundle,
        VariableBundle? projectBundle,
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace)
    {
        var builtIn = BuildBuiltInContext(workflowRunId, issue, project, workspace);
        var merged = VariableBundle.MergeAll(globalBundle, projectBundle, builtIn);
        return ProjectAgentBlocksToConvergedSurface(merged);
    }

    public static VariableBundle Build(
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project,
        WorkspaceIdentity workspace,
        Dictionary<string, object?>? agentConfig = null)
    {
        var filteredAgentConfig = AgentConfigSchema.Filter(agentConfig);
        var userDefaults = filteredAgentConfig is null
            ? VariableBundle.Empty
            : FromRoot(new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
            {
                ["agent"] = JSON.SerializeToElement(filteredAgentConfig),
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
        var root = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["vars"] = bundle.Vars.HasValue && bundle.Vars.Value.ValueKind == JsonValueKind.Object
                ? bundle.Vars.Value.Clone()
                : JSON.DeserializeElement("{}"),
        };

        if (prompts is not null)
            root["prompts"] = JSON.SerializeToElement(prompts);

        return root;
    }

    public static WorkspaceIdentity BuildWorkspaceIdentity(string workflowRunId, MohistIssue issue, WorkflowProjectContext project, string runnerRoot)
    {
        var changeDir = MohistDefaultWorkflowProjection.ChangeDir(issue.Number);
        return new WorkspaceIdentity(
            Path: Mohist.Server.Infrastructure.Workspace.MohistWorkspaceLayout.WorkflowRunWorkspacePath(runnerRoot, workflowRunId),
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
        return FromRoot(new Dictionary<string, JsonElement?>(StringComparer.Ordinal));
    }

    private static VariableBundle FromRoot(Dictionary<string, JsonElement?> variables)
    {
        var varsJson = JSON.Serialize(variables);
        var varsElement = JSON.DeserializeElement(varsJson);
        return new VariableBundle(varsElement);
    }

    /// <summary>
    /// Final-pass projection: walk the merged variable bundle and project
    /// every <c>vars.agent</c> / <c>stages.&lt;stage&gt;.vars.agent</c>
    /// block down to the converged <c>{model, variant}</c> whitelist.
    /// the <c>vars.agent</c> surface only carries
    /// <c>{model, variant}</c>; legacy runtime/liveness keys carried via any
    /// read-in path (ConfigService.GetAgentConfigAsync,
    /// ProjectWorkflowProfileManager.SetVariablesAsync project write path,
    /// already-persisted bundle) MUST be projected away before
    /// <c>vars.agent</c> reaches a downstream dispatch. Legacy keys in
    /// underlying storage remain byte-equivalent — this filter only acts
    /// on the live merged bundle for dispatch.
    /// </summary>
    private static VariableBundle ProjectAgentBlocksToConvergedSurface(VariableBundle bundle)
    {
        var newVars = FilterAgentInVars(bundle.Vars);
        var newStages = FilterStages(bundle.Stages);
        var varsChanged = !JsonElementNullableTextEquals(newVars, bundle.Vars);
        var stagesChanged = !ReferenceEquals(newStages, bundle.Stages);

        if (!varsChanged && !stagesChanged) return bundle;

        return new VariableBundle(newVars, newStages);
    }

    private static Dictionary<string, StageVariables>? FilterStages(Dictionary<string, StageVariables>? stages)
    {
        if (stages is null || stages.Count == 0) return stages;
        var changed = false;
        var result = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in stages)
        {
            if (kvp.Value.Vars is null)
            {
                result[kvp.Key] = kvp.Value;
                continue;
            }

            var filtered = FilterAgentInVars(kvp.Value.Vars);
            if (JsonElementNullableTextEquals(filtered, kvp.Value.Vars))
            {
                result[kvp.Key] = kvp.Value;
            }
            else
            {
                changed = true;
                result[kvp.Key] = new StageVariables(filtered);
            }
        }
        return changed ? result : stages;
    }

    private static JsonElement? FilterAgentInVars(JsonElement? vars)
    {
        if (!vars.HasValue) return vars;
        if (vars.Value.ValueKind != JsonValueKind.Object) return vars;
        if (!vars.Value.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object)
            return vars;

        var filteredDict = AgentConfigSchema.Filter(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(agent.GetRawText(), WorkflowVariableJson.Options));
        if (filteredDict is null)
        {
            if (agent.EnumerateObject().Any())
            {
                var withoutAgent = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in vars.Value.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, "agent", StringComparison.Ordinal))
                        withoutAgent[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                }
                return JsonSerializer.SerializeToElement(withoutAgent, WorkflowVariableJson.Options);
            }
            return vars;
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in vars.Value.EnumerateObject())
        {
            if (string.Equals(prop.Name, "agent", StringComparison.Ordinal))
            {
                dict[prop.Name] = filteredDict;
            }
            else
            {
                dict[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
        }
        return JsonSerializer.SerializeToElement(dict, WorkflowVariableJson.Options);
    }

    private static bool JsonElementNullableTextEquals(JsonElement? a, JsonElement? b)
    {
        if (!a.HasValue && !b.HasValue) return true;
        if (!a.HasValue || !b.HasValue) return false;
        return a.Value.GetRawText() == b.Value.GetRawText();
    }

}
