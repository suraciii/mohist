using System.Globalization;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
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
/// Moving the translation out of <c>WorkflowGrain</c> removes the
/// payload-assembly responsibilities from the control-plane grain
/// (variables, prompts, snapshot construction on the way out; runner-format
/// check parsing + artifact binding on the way in). Template expansion is
/// owned exclusively by the Runner execution pipeline. The inputs are
/// sourced from workflow resolvers / persisted projections, not
/// from grain-exclusive memory.
/// </summary>
public sealed class WorkflowItemTranslator : IScopedService
{
    private readonly WorkflowPromptResolver _promptResolver;
    private readonly WorkflowVariableResolver _variableResolver;
    private readonly IWorkflowArtifactBindService _artifactBindService;
    private readonly IAgentExecutionSnapshotResolver? _agentSnapshots;
    private readonly ILogger<WorkflowItemTranslator> _log;

    public WorkflowItemTranslator(
        WorkflowPromptResolver promptResolver,
        WorkflowVariableResolver variableResolver,
        IWorkflowArtifactBindService artifactBindService,
        ILogger<WorkflowItemTranslator> log)
        : this(promptResolver, variableResolver, artifactBindService, log, null)
    {
    }

    public WorkflowItemTranslator(
        WorkflowPromptResolver promptResolver,
        WorkflowVariableResolver variableResolver,
        IWorkflowArtifactBindService artifactBindService,
        ILogger<WorkflowItemTranslator> log,
        IAgentExecutionSnapshotResolver? agentSnapshots)
    {
        _promptResolver = promptResolver;
        _variableResolver = variableResolver;
        _artifactBindService = artifactBindService;
        _agentSnapshots = agentSnapshots;
        _log = log;
    }

    /// <summary>
    /// Renders a domain <see cref="WorkItem"/> into the runner-process
    /// <see cref="WorkDispatch"/> envelope. Resolves layered variables,
    /// loads prompts, and assembles the snapshot payload that the
    /// runner will render against. The persisted <c>with</c> and
    /// <c>expect</c> declarations are serialized verbatim — the Runner
    /// is the single execution-boundary renderer. The work id is supplied
    /// by the grain (<see cref="WorkItem.Id"/> for tasks or
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

        var payload = await BuildPayloadAsync(item.Stage, workId, "task", item.Title ?? string.Empty, attempt, workflowRunId, run);

        var prompts = await _promptResolver.LoadPromptsAsync(workflowRunId);
        if (prompts.Count > 0)
        {
            var promptsMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in prompts)
                promptsMap[p.Key] = p.Body;
            payload["prompts"] = JSON.SerializeToElement(promptsMap);
        }

        var variables = JSON.Serialize(payload);
        var with = item.With;
        var uses = item.Uses;
        AgentExecutionDefinition? agentDefinition = null;
        if (string.Equals(item.Uses, "mohist/agent", StringComparison.Ordinal))
        {
            (uses, with, agentDefinition) = await ResolveAgentTaskAsync(item, run, workId);
        }
        var withStr = SerializeRaw(with);
        var expectStr = SerializeRaw(item.Expect);
        ValidateLegacyAgentTaskInput(item with { Uses = uses }, workId, with, item.Expect);

        return new WorkDispatch(
            WorkflowRunId: workflowRunId,
            WorkId: workId,
            Uses: uses,
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
            Expect: expectStr,
            AgentDefinition: agentDefinition);
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

        var payload = await BuildPayloadAsync(item.Stage, workId, "checks", "Stage checks", 1, workflowRunId, run);

        var prompts = await _promptResolver.LoadPromptsAsync(workflowRunId);
        if (prompts.Count > 0)
        {
            var promptsMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in prompts)
                promptsMap[p.Key] = p.Body;
            payload["prompts"] = JSON.SerializeToElement(promptsMap);
        }

        var variables = JSON.Serialize(payload);
        var withStr = SerializeRaw(with);

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

    private async Task<Dictionary<string, JsonElement?>>
        BuildPayloadAsync(string stage, string workId, string workType, string title, int attempt,
            string workflowRunId, WorkflowRun run)
    {
        var resolved = await _variableResolver.ResolveEffectiveVariableBundleAsync(workflowRunId, stage);

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var effectiveVarsJson = resolved.Vars ?? JSON.DeserializeElement("{}");

        payload["vars"] = effectiveVarsJson;
        payload["workflow"] = JSON.SerializeToElement(new { runId = workflowRunId });
        payload["stage"] = JSON.SerializeToElement(new { name = stage });
        var work = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = workId,
            ["type"] = workType,
            ["title"] = title,
            ["attempt"] = attempt,
        };
        payload["work"] = JSON.SerializeToElement(work);
        payload["issue"] = JSON.SerializeToElement(new
        {
            projectId = run.Metadata.ProjectId,
            number = ReadIssueNumber(run),
        });
        payload["repository"] = run.Repository is { } repository
            ? JSON.SerializeToElement(new { name = repository.Name, gitUrl = repository.GitUrl, baseBranch = repository.BaseBranch })
            : JSON.SerializeToElement<object?>(null);
        payload["workspace"] = ReadIssueNumber(run) is { } issueNumber
            ? JSON.SerializeToElement(new { name = $"issue-{issueNumber}" })
            : run.Workspace is { } workspace
                ? JSON.SerializeToElement(new { path = workspace.Path, branch = workspace.Branch })
                : JSON.SerializeToElement<object?>(null);

        WorkflowDispatchHelpers.MergeTaskOutputsIntoPayload(payload, run);

        if (workType == "task")
        {
            var stageRun = run.Stages.FirstOrDefault(s => s.Id == stage);
            var task = stageRun?.Tasks.FirstOrDefault(t => t.WorkId == workId || t.Id == workId);
            if (task?.CausedByFailedTaskId is { } failedTaskId)
            {
                var failedTask = FindFailedTask(run, failedTaskId);
                if (failedTask is not null)
                {
                    var failureObj = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                    {
                        ["output"] = failedTask.Output is { } failedOutput && failedOutput.ValueKind == JsonValueKind.Object
                            ? failedOutput.Clone()
                            : null,
                    };
                    var error = failedTask.Error ?? run.Failure?.Error;
                    failureObj["error"] = JSON.SerializeToElement(error ?? new ExecutionError(
                        "task_failed", run.Failure?.Message ?? "The task failed."));
                    payload["failure"] = JSON.SerializeToElement(failureObj);
                }
            }
            if (task?.CausedByFeedbackId is { } feedbackId)
            {
                var feedback = run.Feedback.FirstOrDefault(f => f.Id == feedbackId);
                if (feedback is not null)
                {
                    work["approvalFeedback"] = new
                    {
                        id = feedback.Id,
                        stage = feedback.Stage,
                        createdAt = feedback.CreatedAt.ToString("O"),
                        summary = WorkflowRunExtensions.BuildFeedbackSummary(feedback.Body),
                    };
                    payload["work"] = JSON.SerializeToElement(work);
                }
            }
        }

        return payload;
    }

    private async Task<(string Uses, Dictionary<string, JsonElement?> With, AgentExecutionDefinition Definition)> ResolveAgentTaskAsync(
        WorkItem item,
        WorkflowRun run,
        string workId)
    {
        if (_agentSnapshots is null)
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' references an Agent but Agent resolution is unavailable.",
                new ExecutionError("agent_not_found",
                    $"Workflow task '{workId}' cannot resolve the referenced Agent because Agent resolution is unavailable."));

        var with = item.With;
        if (with is null
            || !with.TryGetValue("name", out var nameElement)
            || nameElement is null
            || nameElement.Value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.Value.GetString())
            || !with.TryGetValue("prompt", out var promptElement)
            || promptElement is null
            || promptElement.Value.ValueKind != JsonValueKind.String)
        {
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' has invalid mohist/agent input.",
                new ExecutionError("invalid_agent_input",
                    $"Workflow task '{workId}' declares an mohist/agent task without a non-empty 'name' or 'prompt'."));
        }

        var projectId = run.Metadata.ProjectId;
        var snapshot = projectId is null
            ? null
            : await _agentSnapshots.ResolveAsync(projectId, nameElement.Value.GetString()!);
        if (snapshot is null)
        {
            var requestedRef = nameElement.Value.GetString()!;
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' references Agent '{requestedRef}' which is not active.",
                new ExecutionError("agent_not_found",
                    $"Workflow task '{workId}' references Agent '{requestedRef}' which does not exist or is archived."));
        }

        var rawPrompt = promptElement.Value.GetString()!;
        var transformed = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["prompt"] = JSON.SerializeToElement(rawPrompt),
        };
        CopyIfPresent(with, transformed, "session");
        CopyIfPresent(with, transformed, "timeout");

        return (snapshot.Runtime switch
        {
            AgentConfigSchema.PiRuntime => "mohist/pi",
            _ => "mohist/opencode",
        }, transformed, snapshot);
    }

    private static void CopyIfPresent(
        Dictionary<string, JsonElement?> source,
        Dictionary<string, JsonElement?> target,
        string key)
    {
        if (source.TryGetValue(key, out var value))
            target[key] = value?.Clone();
    }

    private static string? SerializeRaw(Dictionary<string, JsonElement?>? values) =>
        values is not null && values.Count > 0 ? JSON.Serialize(values) : null;

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
                "Bind the selected Action's 'options' explicitly, e.g. 'options: ${{ vars.agent }}'.",
                new ExecutionError("invalid_input",
                    $"Workflow task '{workId}' declares legacy agent configuration under 'with.agent'. " +
                    "Bind the selected Action's 'options' explicitly."));
        }

        if (with is not null && with.TryGetValue("expect", out var legacyExpect) && legacyExpect.HasValue
            && HasWorkflowCompletionPolicy(legacyExpect.Value))
        {
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' declares Workflow completion policy under 'with.expect'. " +
                "Move 'files', 'markers', and 'failIf' to task-level 'expect'. " +
                "'with.expect' is reserved for Action-owned input on the selected Action contract.",
                new ExecutionError("invalid_input",
                    $"Workflow task '{workId}' declares Workflow completion policy under 'with.expect'. " +
                    "Move 'files', 'markers', and 'failIf' to task-level 'expect'."));
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
                "Remove 'with.kind'; if model configuration is intended, bind 'options: ${{ vars.agent }}'.",
                new ExecutionError("invalid_input",
                    $"Workflow task '{workId}' declares legacy execution discriminator 'with.kind'. " +
                    "Remove 'with.kind'; if model configuration is intended, bind 'options: ${{ vars.agent }}'."));
        }

        if (with is not null && with.ContainsKey("type"))
        {
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' declares legacy execution discriminator 'with.type'. " +
                "The 'mohist/opencode' Action is selected by 'uses' and does not read 'type'. " +
                "Remove 'with.type'; if model configuration is intended, bind 'options: ${{ vars.agent }}'.",
                new ExecutionError("invalid_input",
                    $"Workflow task '{workId}' declares legacy execution discriminator 'with.type'. " +
                    "Remove 'with.type'; if model configuration is intended, bind 'options: ${{ vars.agent }}'."));
        }

        _ = expect;
    }

    private static bool IsInlineAgentUses(string? uses) =>
        string.Equals(uses, "mohist/opencode", StringComparison.Ordinal)
        || string.Equals(uses, "mohist/pi", StringComparison.Ordinal);

    private static bool HasWorkflowCompletionPolicy(JsonElement expectElement)
    {
        if (expectElement.ValueKind != JsonValueKind.Object) return false;
        return expectElement.TryGetProperty("files", out _)
            || expectElement.TryGetProperty("markers", out _)
            || (expectElement.TryGetProperty("failIf", out var failIf)
                && failIf.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(failIf.GetString()));
    }

    private static int? ReadIssueNumber(WorkflowRun run) =>
        run.Metadata.IssueNumber is > 0 ? run.Metadata.IssueNumber : null;

    private static int? ReadEpicNumber(WorkflowRun run) =>
        run.Metadata.EpicNumber is > 0 ? run.Metadata.EpicNumber : null;

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

        // Validate before binding artifacts so malformed Action output cannot
        // produce durable artifact side effects.
        if (!TryCanonicalizeTaskOutput(result.Output, out var validatedOutput, out var shapeError))
        {
            return new InboundReport.Task(new TaskReport(
                WorkId: workId,
                Status: TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: shapeError,
                Error: new ExecutionError("unexpected-error", shapeError)));
        }

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
                projectId: run.Metadata.ProjectId,
                issueNumber: ReadIssueNumber(run));

            if (!bindResult.IsSuccess)
            {
                _log.LogWarning(
                    "run {run} work {work} artifact binding failed: {reason}",
                    workflowRunId, workId, bindResult.Error);
                return new InboundReport.Task(new TaskReport(
                    WorkId: workId,
                    Status: TaskReportStatus.Failed,
                    Output: null,
                    Artifacts: null,
                    Detail: bindResult.Error ?? "artifact binding failed",
                    Error: result.Error));
            }

            artifacts = bindResult.ArtifactRecordedEvents
                .Select(e => new ArtifactRef(Path: e.Path))
                .ToList();
        }

        return new InboundReport.Task(new TaskReport(
            WorkId: workId,
            Status: status,
            Output: validatedOutput,
            Artifacts: artifacts,
            Detail: detail,
            AddTasks: result.AddTasks is { Count: > 0 } ? result.AddTasks.ToList() : null,
            Error: result.Error));
    }

    private static InboundReport TranslateChecksResult(WorkItem item, WorkResult result)
    {
        if (!HasValidCheckResultRows(item.Items, result.Output))
            return MalformedCheckOutput(item);

        var results = WorkflowDispatchHelpers.ParseCheckResults(result.Output);
        return new InboundReport.Checks(new CheckReport(item.Stage, results));
    }

    private static bool HasValidCheckResultRows(IReadOnlyList<CheckItem>? checks, JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Array }) return false;
        var expectedNames = (checks ?? []).Select(check => check.Name).ToHashSet(StringComparer.Ordinal);
        if (expectedNames.Count != (checks?.Count ?? 0)) return false;

        var reportedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in output.Value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
                return false;

            var nameValue = name.GetString()!;
            if (!expectedNames.Contains(nameValue) || !reportedNames.Add(nameValue))
                return false;

            if (row.TryGetProperty("status", out var status)
                && status.ValueKind != JsonValueKind.String)
                return false;

            if (row.TryGetProperty("message", out var message)
                && message.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                return false;

            if (row.TryGetProperty("output", out var actionOutput)
                && actionOutput.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
                return false;

            if (row.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Null) continue;
                if (error.ValueKind != JsonValueKind.Object
                    || !error.TryGetProperty("code", out var code)
                    || code.ValueKind != JsonValueKind.String
                    || !error.TryGetProperty("message", out var errorMessage)
                    || errorMessage.ValueKind != JsonValueKind.String)
                    return false;
            }
        }

        return reportedNames.SetEquals(expectedNames);
    }

    private static InboundReport MalformedCheckOutput(WorkItem item)
    {
        const string message = "Runner reported an invalid check output shape. Check output must be a JSON array of named rows with object-or-null Action output.";
        var error = new ExecutionError("unexpected-error", message);
        var failed = (item.Items ?? [])
            .Select(check => new CheckResult(check.Name, CheckResultStatus.Failed, message, Error: error))
            .ToList();
        return new InboundReport.Checks(new CheckReport(item.Stage, failed));
    }

    /// <summary>
    /// Canonicalize a Workflow task output element to the storage contract:
    /// object-or-null. An explicit JSON null becomes nullable null; a
    /// missing value becomes nullable null. Any other shape (array,
    /// scalar, string) fails the call so the caller can convert it into a
    /// durable failed task report.
    /// </summary>
    internal static bool TryCanonicalizeTaskOutput(JsonElement? output, out JsonElement? canonical, out string error)
    {
        if (!output.HasValue) { canonical = null; error = ""; return true; }
        var element = output.Value;
        if (element.ValueKind == JsonValueKind.Null) { canonical = null; error = ""; return true; }
        if (element.ValueKind == JsonValueKind.Object) { canonical = element.Clone(); error = ""; return true; }
        canonical = null;
        error = "Runner reported an invalid Action output shape. Successful Action output must be a JSON object or null.";
        return false;
    }

    private static TaskReportStatus ResolveTaskReportStatus(WorkResult result) =>
        string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
                ? TaskReportStatus.Succeeded
                : TaskReportStatus.Failed;

    private static string? NormalizeDetail(WorkResult result, TaskReportStatus status)
    {
        if (status == TaskReportStatus.Succeeded) return null;
        if (result.Error is not null) return result.Error.Message;
        if (!string.IsNullOrWhiteSpace(result.Message)) return result.Message;
        return result.Status;
    }

    private async Task<JsonElement?> ResolveBindVariablesAsync(
        string workflowRunId, WorkflowRun run, string stage)
    {
        var resolved = await _variableResolver.ResolveEffectiveVariableBundleAsync(workflowRunId, stage);
        return resolved.Vars;
    }
}
