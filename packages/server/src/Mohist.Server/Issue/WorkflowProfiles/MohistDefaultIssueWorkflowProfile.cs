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
    public WorkflowDefinitionInput Definition => MohistWorkflow.Definition;

    public string BuildVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project, Dictionary<string, object?>? globalAgentConfig = null)
    {
        var agentConfig = BuildAgentConfig(issue, globalAgentConfig);

        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { system = "mohist", runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number, title = issue.Title, body = issue.Body ?? "" }, WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(new { id = project.Id, name = project.Name, path = project.Path, baseBranch = project.BaseBranch, defaultBranch = project.BaseBranch }, WorkflowVariableJson.Options),
            ["openspecChangeName"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeName(issue.Number), WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeDir(issue.Number), WorkflowVariableJson.Options),
            ["agent"] = JsonSerializer.SerializeToElement(agentConfig, WorkflowVariableJson.Options),
            ["vars"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>(), WorkflowVariableJson.Options),
        };
        return JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
    }

    public Dictionary<string, Dictionary<string, string>>? BuildStageVariables(Domain.Issue issue, Dictionary<string, Dictionary<string, object?>>? globalStageAgentConfigs = null)
    {
        var result = CopyStageVariables(issue.StageVariables);

        if (globalStageAgentConfigs is not null)
        {
            foreach (var (stage, config) in globalStageAgentConfigs)
                MergeStageAgent(result, stage, config);
        }

        if (issue.StageModels is not null)
        {
            foreach (var (stage, model) in issue.StageModels)
            {
                if (!string.IsNullOrWhiteSpace(model))
                    MergeStageAgent(result, stage, new Dictionary<string, object?>(StringComparer.Ordinal) { ["model"] = model });
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static AgentVariableConfig BuildAgentConfig(Domain.Issue issue, Dictionary<string, object?>? globalAgentConfig)
    {
        var opencodeConfig = new Dictionary<string, object?>(StringComparer.Ordinal);
        MergeAgentConfig(opencodeConfig, globalAgentConfig);
        MergeAgentConfig(opencodeConfig, issue.AgentConfig);

        // Legacy compatibility: issue.model is equivalent to agent.model.
        if (!string.IsNullOrWhiteSpace(issue.Model))
            opencodeConfig["model"] = issue.Model;

        return new AgentVariableConfig(Default: "opencode", Opencode: opencodeConfig);
    }

    private static Dictionary<string, Dictionary<string, string>> CopyStageVariables(Dictionary<string, Dictionary<string, string>>? source)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;

        foreach (var (stage, sections) in source)
            result[stage] = new Dictionary<string, string>(sections, StringComparer.Ordinal);

        return result;
    }

    private static void MergeStageAgent(Dictionary<string, Dictionary<string, string>> stageVariables, string stage, Dictionary<string, object?> agentConfig)
    {
        if (!stageVariables.TryGetValue(stage, out var sections))
        {
            sections = new Dictionary<string, string>(StringComparer.Ordinal);
            stageVariables[stage] = sections;
        }

        if (sections.TryGetValue("agent", out var existingJson))
            agentConfig = MergeAgentJson(existingJson, agentConfig);

        sections["agent"] = JsonSerializer.Serialize(new AgentVariableConfig("opencode", agentConfig), WorkflowVariableJson.Options);
    }

    private static Dictionary<string, object?> MergeAgentJson(string existingJson, Dictionary<string, object?> overrideConfig)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<AgentVariableConfig>(existingJson, WorkflowVariableJson.Options);
            var merged = parsed?.Opencode is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(parsed.Opencode, StringComparer.Ordinal);
            MergeAgentConfig(merged, overrideConfig);
            return merged;
        }
        catch
        {
            return new Dictionary<string, object?>(overrideConfig, StringComparer.Ordinal);
        }
    }

    private static void MergeAgentConfig(Dictionary<string, object?> target, Dictionary<string, object?>? source)
    {
        if (source is null) return;
        foreach (var (key, value) in source)
        {
            if (value is null) continue;
            target[key] = NormalizeJsonValue(value);
        }
    }

    private static object? NormalizeJsonValue(object? value) => value switch
    {
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var n) => n,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), WorkflowVariableJson.Options),
            JsonValueKind.Array => JsonSerializer.Deserialize<object?[]>(element.GetRawText(), WorkflowVariableJson.Options),
            _ => null,
        },
        _ => value,
    };

    public MohistDefaultWorkflowState ProjectWorkflowState(Domain.Issue issue, WorkflowStatusSnapshot? workflow) =>
        MohistDefaultWorkflowProjection.ProjectWorkflowState(
            issue.Number,
            issue.Title,
            IssueDomainNames.Stage(issue.Stage),
            issue.Attention,
            issue.BlockedReason,
            workflow);

    public MohistDefaultWorkflowState ProjectWorkflowState(IssueReadModel issue, WorkflowStatusSnapshot? workflow) =>
        MohistDefaultWorkflowProjection.ProjectWorkflowState(
            issue.Number,
            issue.Title,
            issue.Stage,
            issue.Attention,
            issue.BlockedReason,
            workflow);
}

public sealed record AgentVariableConfig(
    string Default,
    Dictionary<string, object?> Opencode
);
