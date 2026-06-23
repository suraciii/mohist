using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Workflow.Grains;

internal sealed record WorkDispatchRequest(
    string Stage,
    string LogicalId,
    string WorkType,
    string Title,
    string? Uses,
    Dictionary<string, JsonElement?>? With,
    TaskArtifactCapture? Artifacts = null,
    List<TaskOutputDefinition>? Outputs = null,
    Dictionary<string, string>? SetVars = null,
    string? WorkIdOverride = null);

public sealed class WorkflowDispatchBuilder(
    WorkflowProfileManager profileManager,
    ILogger<WorkflowDispatchBuilder> log)
{
    internal async Task<WorkDispatch> BuildAsync(
        WorkDispatchRequest req,
        string workflowRunId,
        WorkflowRun run)
    {
        var workId = req.WorkIdOverride ?? (req.WorkType == "task" ? req.LogicalId : $"{req.LogicalId}:{Guid.NewGuid():N}");
        var attempt = req.WorkType == "task" ? WorkflowDispatchHelpers.TaskAttempt(req.LogicalId) : 1;

        var (payload, effectiveVarsJson, resolved) = await BuildPayloadAsync(req, workId, attempt, workflowRunId, run);

        var prompts = await profileManager.LoadPromptsAsync(workflowRunId);
        if (prompts.Count > 0)
        {
            var promptsMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in prompts)
                promptsMap[p.Key] = p.Body;
            payload["prompts"] = JSON.SerializeToElement(promptsMap);
        }

        var variables = JSON.Serialize(payload);
        var withStr = ResolveWith(effectiveVarsJson, resolved, req.With, req.Stage, workId, workflowRunId);

        return new WorkDispatch(
            WorkflowRunId: workflowRunId,
            WorkId: workId,
            Uses: req.Uses,
            With: withStr,
            Variables: variables,
            WorkType: req.WorkType,
            Stage: req.Stage,
            Title: req.Title,
            Issue: WorkflowDispatchHelpers.BuildIssueRef(payload),
            Artifacts: req.Artifacts is not null && !req.Artifacts.IsEmpty ? JSON.Serialize(req.Artifacts) : null,
            Outputs: req.Outputs is not null && req.Outputs.Count > 0 ? JSON.Serialize(req.Outputs) : null,
            SetVars: req.SetVars is not null && req.SetVars.Count > 0 ? JSON.Serialize(req.SetVars) : null);
    }

    internal static WorkDispatchRequest BuildChecksRequest(
        string stage,
        IReadOnlyList<CheckItem> items,
        string? workIdOverride = null)
    {
        var checksPayload = items.Select(i => new Dictionary<string, JsonElement?>
        {
            ["name"] = JSON.SerializeToElement(i.Name),
            ["title"] = JSON.SerializeToElement(i.Title),
            ["uses"] = i.Uses is not null ? JSON.SerializeToElement(i.Uses) : null,
            ["with"] = i.With is not null ? JSON.SerializeToElement(i.With) : null,
        }).ToList();

        return new WorkDispatchRequest(
            stage, $"checks-{stage}", "checks", "Stage checks",
            Uses: null,
            With: new Dictionary<string, JsonElement?> { ["checks"] = JSON.SerializeToElement(checksPayload) },
            WorkIdOverride: workIdOverride);
    }

    private async Task<(Dictionary<string, JsonElement?> Payload, JsonElement EffectiveVars, VariableBundle Resolved)>
        BuildPayloadAsync(WorkDispatchRequest req, string workId, int attempt, string workflowRunId, WorkflowRun run)
    {
        var template = await profileManager.LoadTemplateAsync(workflowRunId);
        var independent = await profileManager.LoadVariablesAsync(workflowRunId);
        var embedded = template.EmbeddedVariables ?? VariableBundle.Empty;
        var resolved = VariableBundle.Patch(embedded, independent);

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var effectiveVarsJson = WorkflowDispatchHelpers.ResolveEffectiveStageVars(resolved, req.Stage)
            ?? JSON.DeserializeElement("{}");

        if (effectiveVarsJson.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in effectiveVarsJson.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["vars"] = effectiveVarsJson;
        payload["workflow"] = JSON.SerializeToElement(new { runId = workflowRunId });
        payload["stage"] = JSON.SerializeToElement(new { name = req.Stage });
        payload["work"] = JSON.SerializeToElement(new { id = workId, type = req.WorkType, title = req.Title, attempt });

        MergeTaskOutputsIntoPayload(payload, run);

        if (req.WorkType == "task")
        {
            var stageRun = run.Stages.FirstOrDefault(s => s.Id == req.Stage);
            var task = stageRun?.Tasks.FirstOrDefault(t => t.Id == req.LogicalId);
            if (task?.CausedByFeedbackId is { } feedbackId)
            {
                var feedback = run.Feedback.FirstOrDefault(f => f.Id == feedbackId);
                if (feedback is not null)
                {
                    var issueNumber = TryGetAnnotation(run, "issueNumber", out var numStr) && int.TryParse(numStr, out var n) ? (int?)n : null;
                    var projectId = TryGetAnnotation(run, "projectId", out var pid) ? pid : "";

                    payload["approvalFeedback"] = JSON.SerializeToElement(new
                    {
                        id = feedback.Id,
                        stage = feedback.Stage,
                        createdAt = feedback.CreatedAt.ToString("O"),
                        summary = WorkflowRunExtensions.BuildFeedbackSummary(feedback.Body),
                        command = WorkflowRunExtensions.BuildFeedbackShowCommand(issueNumber, feedback.Id, projectId),
                    });
                }
            }
        }

        return (payload, effectiveVarsJson, resolved);
    }

    private string? ResolveWith(
        JsonElement effectiveVarsJson,
        VariableBundle resolved,
        Dictionary<string, JsonElement?>? with,
        string stage,
        string workId,
        string workflowRunId)
    {
        var effectiveBundle = effectiveVarsJson.ValueKind == JsonValueKind.Object
            ? new VariableBundle(effectiveVarsJson)
            : VariableBundle.Empty;

        var dispatchWith = with is not null
            ? new Dictionary<string, JsonElement?>(with, StringComparer.Ordinal)
            : null;
        dispatchWith = WorkflowProfileManager.ExpandTaskWith(effectiveBundle, dispatchWith);

        if ((dispatchWith is null || !dispatchWith.ContainsKey("agent"))
            && effectiveVarsJson.ValueKind == JsonValueKind.Object
            && effectiveVarsJson.TryGetProperty("agent", out var effectiveAgentEl)
            && effectiveAgentEl.ValueKind == JsonValueKind.Object)
        {
            dispatchWith ??= new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            dispatchWith["agent"] = effectiveAgentEl.Clone();
        }

        var withStr = dispatchWith is not null ? JSON.Serialize(dispatchWith) : null;
        LogAgentDiagnostics(stage, workId, dispatchWith, effectiveVarsJson, resolved, workflowRunId);
        return withStr;
    }

    private void LogAgentDiagnostics(
        string stage,
        string workId,
        Dictionary<string, JsonElement?>? dispatchWith,
        JsonElement effectiveVarsJson,
        VariableBundle resolved,
        string workflowRunId)
    {
        var withModel = WorkflowDispatchHelpers.TryReadNestedString(dispatchWith, "agent", "model");
        var varsModel = WorkflowDispatchHelpers.TryReadNestedString(effectiveVarsJson, "agent", "model");
        var stageModel = WorkflowDispatchHelpers.TryReadStageAgentModel(resolved, stage);
        var source = !string.IsNullOrWhiteSpace(withModel)
            ? "with.agent.model"
            : !string.IsNullOrWhiteSpace(varsModel)
                ? "vars.agent.model"
                : !string.IsNullOrWhiteSpace(stageModel.Value)
                    ? "stage.vars.agent.model"
                    : "none";

        log.LogInformation(
            "Workflow {WorkflowId} dispatch {WorkId} stage={Stage} agent model diagnostics: with={WithModel}, vars={VarsModel}, stageOverride={StageModel}, source={Source}",
            workflowRunId,
            workId,
            stage,
            withModel ?? "(null)",
            varsModel ?? "(null)",
            stageModel.Present
                ? stageModel.Value ?? "(null override)"
                : "(missing)",
            source);
    }

    internal static void MergeTaskOutputsIntoPayload(Dictionary<string, JsonElement?> payload, WorkflowRun run)
    {
        var tasksMap = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var stage in run.Stages)
        {
            foreach (var task in stage.Tasks)
            {
                if (task.Status != TaskRunStatus.Completed || !task.Output.HasValue)
                    continue;

                if (task.Output.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var outputs = WorkflowDispatchHelpers.JsonElementToObject(task.Output.Value);
                if (outputs is Dictionary<string, object?> dict)
                {
                    tasksMap[task.DefinitionId] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["outputs"] = dict
                    };
                }
            }
        }

        if (tasksMap.Count > 0)
        {
            payload["tasks"] = JSON.SerializeToElement(tasksMap);
        }
    }

    private static bool TryGetAnnotation(WorkflowRun run, string key, out string value)
    {
        value = "";
        return run.Metadata?.Annotations?.TryGetValue(key, out value!) == true;
    }
}
