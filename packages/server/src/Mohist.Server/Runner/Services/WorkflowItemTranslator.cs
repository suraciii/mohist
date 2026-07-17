using System.Globalization;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Translator that owns the boundary between the control plane's
/// domain-semantic work items and the runner-process execution envelopes.
/// The control plane (WorkflowGrain) exposes
/// <see cref="WorkItem"/> / <see cref="TaskReport"/> / <see cref="CheckReport"/>;
/// this service renders work items into <see cref="WorkDispatch"/> for the
/// runner process and converts the runner's raw <see cref="WorkResult"/> into
/// domain reports the grain consumes.
///
/// Moving the translation out of <c>WorkflowGrain</c> removes the rendering
/// and parsing responsibilities from the control-plane grain (variables,
/// prompts, with-template expansion, payload assembly on the way out;
/// runner-format check parsing + artifact binding on the way in). The
/// inputs are sourced from <c>WorkflowProfileManager</c> / persisted
/// projections, not from grain-exclusive memory.
/// </summary>
public sealed class WorkflowItemTranslator : IScopedService
{
    private readonly WorkflowProfileManager _profileManager;
    private readonly IWorkflowArtifactBindService _artifactBindService;
    private readonly ILogger<WorkflowItemTranslator> _log;

    public WorkflowItemTranslator(
        WorkflowProfileManager profileManager,
        IWorkflowArtifactBindService artifactBindService,
        ILogger<WorkflowItemTranslator> log)
    {
        _profileManager = profileManager;
        _artifactBindService = artifactBindService;
        _log = log;
    }

    /// <summary>
    /// Renders a domain <see cref="WorkItem"/> into the runner-process
    /// <see cref="WorkDispatch"/> envelope. Resolves layered variables,
    /// loads prompts, expands <c>with</c> templates, and assembles the
    /// payload the action will consume. The work id is supplied by the
    /// grain (<see cref="WorkItem.Id"/> for tasks or
    /// <see cref="WorkItem.Items"/> for checks); this translator never
    /// invents a dispatch id of its own.
    /// </summary>
    public async Task<WorkDispatch> TranslateToDispatchAsync(
        WorkItem item,
        string workflowRunId,
        WorkflowRun run,
        string runnerId)
    {
        if (item.IsTask)
            return await BuildTaskDispatchAsync(item, workflowRunId, run, runnerId);
        if (item.IsChecks)
            return await BuildChecksDispatchAsync(item, workflowRunId, run, runnerId);
        throw new InvalidOperationException(
            $"Unsupported work item variant '{item.WorkType}' for workflow '{workflowRunId}'");
    }

    private async Task<WorkDispatch> BuildTaskDispatchAsync(
        WorkItem item,
        string workflowRunId,
        WorkflowRun run,
        string runnerId)
    {
        var workId = item.Id ?? throw new InvalidOperationException(
            $"Task work item for workflow '{workflowRunId}' is missing work id");
        var attempt = WorkflowDispatchHelpers.TaskAttempt(workId);

        var (payload, effectiveVarsJson, resolved) =
            await BuildPayloadAsync(item.Stage, workId, "task", item.Title ?? string.Empty, attempt, workflowRunId, run);

        var prompts = await _profileManager.LoadPromptsAsync(workflowRunId);
        if (prompts.Count > 0)
        {
            var promptsMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in prompts)
                promptsMap[p.Key] = p.Body;
            payload["prompts"] = JSON.SerializeToElement(promptsMap);
        }

        var variables = JSON.Serialize(payload);
        var bundle = BuildVariableBundle(effectiveVarsJson);
        var withStr = ExpandToJson(bundle, item.With);
        var expectStr = ExpandToJson(bundle, item.Expect);
        ValidateLegacyAgentTaskInput(item, workId, item.With, item.Expect);

        return new WorkDispatch(
            WorkflowRunId: workflowRunId,
            WorkId: workId,
            Uses: item.Uses,
            With: withStr,
            Variables: variables,
            WorkType: "task",
            Stage: item.Stage,
            Title: item.Title,
            Issue: WorkflowDispatchHelpers.BuildIssueRef(payload),
            Artifacts: item.Artifacts is not null && !item.Artifacts.IsEmpty ? JSON.Serialize(item.Artifacts) : null,
            SetVars: item.SetVars is not null && item.SetVars.Count > 0 ? JSON.Serialize(item.SetVars) : null,
            OwnerKind: WorkDispatchOwnerKinds.Workflow,
            AgentJobId: null,
            Recovery: item.Recovery is not null ? JSON.Serialize(item.Recovery) : null,
            RecoveryRemaining: item.RecoveryRemaining,
EpicNumber: ReadEpicNumber(run),
            Expect: expectStr);
    }

    private async Task<WorkDispatch> BuildChecksDispatchAsync(
        WorkItem item,
        string workflowRunId,
        WorkflowRun run,
        string runnerId)
    {
        var workId = item.Id ?? throw new InvalidOperationException(
            $"Checks work item for workflow '{workflowRunId}' is missing work id");
        var items = item.Items ?? new List<CheckItem>();
        var checksPayload = items.Select(i => new Dictionary<string, JsonElement?>
        {
            ["name"] = JSON.SerializeToElement(i.Name),
            ["title"] = JSON.SerializeToElement(i.Title),
            ["uses"] = i.Uses is not null ? JSON.SerializeToElement(i.Uses) : null,
            ["with"] = i.With is not null ? JSON.SerializeToElement(i.With) : null,
        }).ToList();

        var with = new Dictionary<string, JsonElement?>
        {
            ["checks"] = JSON.SerializeToElement(checksPayload),
        };

        var (payload, effectiveVarsJson, resolved) =
            await BuildPayloadAsync(item.Stage, workId, "checks", "Stage checks", 1, workflowRunId, run);

        var prompts = await _profileManager.LoadPromptsAsync(workflowRunId);
        if (prompts.Count > 0)
        {
            var promptsMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in prompts)
                promptsMap[p.Key] = p.Body;
            payload["prompts"] = JSON.SerializeToElement(promptsMap);
        }

        var variables = JSON.Serialize(payload);
        var bundle = BuildVariableBundle(effectiveVarsJson);
        var withStr = ExpandToJson(bundle, with);

        return new WorkDispatch(
            WorkflowRunId: workflowRunId,
            WorkId: workId,
            Uses: null,
            With: withStr,
            Variables: variables,
            WorkType: "checks",
            Stage: item.Stage,
            Title: "Stage checks",
            Issue: WorkflowDispatchHelpers.BuildIssueRef(payload),
            OwnerKind: WorkDispatchOwnerKinds.Workflow,
            AgentJobId: null,
            EpicNumber: ReadEpicNumber(run));
    }

    private async Task<(Dictionary<string, JsonElement?> Payload, JsonElement EffectiveVars, VariableBundle Resolved)>
        BuildPayloadAsync(string stage, string workId, string workType, string title, int attempt,
            string workflowRunId, WorkflowRun run)
    {
        var resolved = await _profileManager.ResolveLayeredVariablesAsync(workflowRunId);

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var effectiveVarsJson = resolved.ResolveStageVars(stage)
            ?? JSON.DeserializeElement("{}");

        if (effectiveVarsJson.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in effectiveVarsJson.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["vars"] = effectiveVarsJson;
        payload["workflow"] = JSON.SerializeToElement(new { runId = workflowRunId });
        payload["stage"] = JSON.SerializeToElement(new { name = stage });
        payload["work"] = JSON.SerializeToElement(new { id = workId, type = workType, title, attempt });

        WorkflowDispatchHelpers.MergeTaskOutputsIntoPayload(payload, run);

        if (workType == "task")
        {
            var stageRun = run.Stages.FirstOrDefault(s => s.Id == stage);
            var task = stageRun?.Tasks.FirstOrDefault(t => t.WorkId == workId || t.Id == workId);
            if (task?.CausedByFailedTaskId is { } failedTaskId)
            {
                var failedTask = FindFailedTask(run, failedTaskId);
                if (failedTask?.Output is { } failedOutput && failedOutput.ValueKind == JsonValueKind.Object)
                {
                    var failureObj = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                    {
                        ["output"] = failedOutput.Clone(),
                    };
                    payload["failure"] = JSON.SerializeToElement(failureObj);
                }
            }
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

    private string? ExpandToJson(VariableBundle? effectiveBundle, Dictionary<string, JsonElement?>? values)
    {
        var expanded = WorkflowProfileManager.ExpandTaskWith(effectiveBundle, values);
        return expanded is not null && expanded.Count > 0 ? JSON.Serialize(expanded) : null;
    }

    private static VariableBundle BuildVariableBundle(JsonElement effectiveVarsJson) =>
        effectiveVarsJson.ValueKind == JsonValueKind.Object
            ? new VariableBundle(effectiveVarsJson)
            : VariableBundle.Empty;

    private static void ValidateLegacyAgentTaskInput(
        WorkItem item,
        string workId,
        Dictionary<string, JsonElement?>? with,
        Dictionary<string, JsonElement?>? expect)
    {
        if (!IsInlineAgentUses(item.Uses))
            return;

        if (with is not null && with.TryGetValue("agent", out var legacyAgent) && legacyAgent.HasValue)
        {
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' declares legacy agent configuration under 'with.agent'. " +
                "Bind the selected Action's 'options' explicitly, e.g. 'options: ${{ vars.agent }}'.");
        }

        if (with is not null && with.TryGetValue("expect", out var legacyExpect) && legacyExpect.HasValue
            && HasWorkflowCompletionPolicy(legacyExpect.Value))
        {
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' declares Workflow completion policy under 'with.expect'. " +
                "Move 'files', 'markers', and 'failIf' to task-level 'expect'. " +
                "'with.expect' is reserved for Action-owned input on the selected Action contract.");
        }

        // Spec scenario "Legacy agent input is invalid": persisted or
        // in-flight inline-agent tasks that bypassed profile ingestion
        // MUST fail dispatch with the same actionable errors as profile
        // loading. `kind` and `type` are legacy execution-backend
        // discriminators the inline-agent contract does not read.
        if (with is not null && with.ContainsKey("kind"))
        {
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' declares legacy execution discriminator 'with.kind'. " +
                "The 'mohist/opencode' Action is selected by 'uses' and does not read 'kind'. " +
                "Remove 'with.kind'; if model configuration is intended, bind 'options: ${{ vars.agent }}'.");
        }

        if (with is not null && with.ContainsKey("type"))
        {
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' declares legacy execution discriminator 'with.type'. " +
                "The 'mohist/opencode' Action is selected by 'uses' and does not read 'type'. " +
                "Remove 'with.type'; if model configuration is intended, bind 'options: ${{ vars.agent }}'.");
        }

        _ = expect;
    }

    private static bool IsInlineAgentUses(string? uses) =>
        string.Equals(uses, "mohist/opencode", StringComparison.Ordinal)
        || string.Equals(uses, "mohist/acp-agent", StringComparison.Ordinal);

    private static bool HasWorkflowCompletionPolicy(JsonElement expectElement)
    {
        if (expectElement.ValueKind != JsonValueKind.Object) return false;
        return expectElement.TryGetProperty("files", out _)
            || expectElement.TryGetProperty("markers", out _)
            || (expectElement.TryGetProperty("failIf", out var failIf)
                && failIf.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(failIf.GetString()));
    }

    private static bool TryGetAnnotation(WorkflowRun run, string key, out string value)
    {
        value = "";
        return run.Metadata?.Annotations?.TryGetValue(key, out value!) == true;
    }

    private static int? ReadIssueNumber(WorkflowRun run) =>
        TryGetAnnotation(run, "issueNumber", out var raw)
        && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0
            ? number
            : null;

    private static int? ReadEpicNumber(WorkflowRun run) =>
        TryGetAnnotation(run, "epicNumber", out var raw)
        && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0
            ? number
            : null;

    private static TaskRun? FindFailedTask(WorkflowRun run, string taskId)
    {
        foreach (var stage in run.Stages)
        {
            var task = stage.Tasks.FirstOrDefault(t => t.Id == taskId || t.WorkId == taskId);
            if (task is not null && task.Output.HasValue) return task;
        }
        return null;
    }

    public abstract record InboundReport
    {
        public sealed record Task(TaskReport Value) : InboundReport;
        public sealed record Checks(CheckReport Value) : InboundReport;
    }

    public async Task<InboundReport> TranslateResultAsync(
        WorkItem item,
        WorkResult result,
        string workflowRunId,
        WorkflowRun run)
    {
        if (item.IsTask)
            return await TranslateTaskResultAsync(item, result, workflowRunId, run);
        if (item.IsChecks)
            return TranslateChecksResult(item, result);
        throw new InvalidOperationException(
            $"Unsupported work item variant '{item.WorkType}' for workflow '{workflowRunId}'");
    }

    private async Task<InboundReport> TranslateTaskResultAsync(
        WorkItem item,
        WorkResult result,
        string workflowRunId,
        WorkflowRun run)
    {
        var workId = item.Id ?? throw new InvalidOperationException(
            $"Task work item for workflow '{workflowRunId}' is missing work id");
        var status = ResolveTaskReportStatus(result);
        var detail = NormalizeDetail(result, status);
        IReadOnlyList<ArtifactRef>? artifacts = null;

        if (result.ArtifactUploadIds is { Length: > 0 })
        {
            var bindResult = await _artifactBindService.BindAsync(
                workflowRunId,
                workId,
                workId,
                result.ArtifactUploadIds,
                item.Artifacts,
                variables: await ResolveBindVariablesAsync(workflowRunId, run, item.Stage),
                projectId: run.Metadata?.Annotations?.GetValueOrDefault("projectId"),
                issueNumber: ReadIssueNumber(run));

            if (!bindResult.IsSuccess)
            {
                _log.LogWarning(
                    "Workflow {Id} task {TaskId} artifact binding failed: {Error}",
                    workflowRunId, workId, bindResult.Error);
                return new InboundReport.Task(new TaskReport(
                    WorkId: workId,
                    Status: TaskReportStatus.Failed,
                    Output: result.Output,
                    Artifacts: null,
                    Detail: bindResult.Error ?? "artifact binding failed"));
            }

            artifacts = bindResult.ArtifactRecordedEvents
                .Select(e => new ArtifactRef(Path: e.Path))
                .ToList();
        }

        return new InboundReport.Task(new TaskReport(
            WorkId: workId,
            Status: status,
            Output: result.Output,
            Artifacts: artifacts,
            Detail: detail,
            AddTasks: result.AddTasks is { Count: > 0 } ? result.AddTasks.ToList() : null));
    }

    private static InboundReport TranslateChecksResult(WorkItem item, WorkResult result)
    {
        var results = WorkflowDispatchHelpers.ParseCheckResults(result.Output);
        return new InboundReport.Checks(new CheckReport(item.Stage, results));
    }

    private static TaskReportStatus ResolveTaskReportStatus(WorkResult result) =>
        string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
                ? TaskReportStatus.Succeeded
                : TaskReportStatus.Failed;

    private static string? NormalizeDetail(WorkResult result, TaskReportStatus status)
    {
        if (status == TaskReportStatus.Succeeded) return null;
        if (!string.IsNullOrWhiteSpace(result.Message)) return result.Message;
        return result.Status;
    }

    private async Task<JsonElement?> ResolveBindVariablesAsync(
        string workflowRunId, WorkflowRun run, string stage)
    {
        var resolved = await _profileManager.ResolveLayeredVariablesAsync(workflowRunId);
        return resolved.ResolveStageVars(stage);
    }
}
