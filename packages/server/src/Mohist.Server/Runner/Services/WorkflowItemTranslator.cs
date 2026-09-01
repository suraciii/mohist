using System.Globalization;
using System.Text.Json;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
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
    private readonly IAgentExecutionSnapshotResolver? _agentSnapshots;

    public WorkflowItemTranslator(
        WorkflowPromptResolver promptResolver,
        WorkflowVariableResolver variableResolver)
        : this(promptResolver, variableResolver, null)
    {
    }

    public WorkflowItemTranslator(
        WorkflowPromptResolver promptResolver,
        WorkflowVariableResolver variableResolver,
        IAgentExecutionSnapshotResolver? agentSnapshots)
    {
        _promptResolver = promptResolver;
        _variableResolver = variableResolver;
        _agentSnapshots = agentSnapshots;
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

    internal async Task<WorkDispatch> TranslateToDispatchPreviewAsync(
        WorkItem item,
        string workflowRunId,
        WorkflowRun run)
    {
        if (!item.IsTask && !item.IsChecks)
            throw new InvalidOperationException(
                $"Unsupported work item variant '{item.WorkType}' for workflow '{workflowRunId}'");

        var workId = item.Id ?? throw new InvalidOperationException(
            $"Workflow work item for workflow '{workflowRunId}' is missing work id");
        var workType = item.IsTask ? "task" : "checks";
        var title = item.IsTask ? item.Title ?? string.Empty : "Stage checks";
        var payload = await BuildPayloadAsync(
            item.Stage,
            workId,
            workType,
            title,
            item.IsTask ? WorkflowDispatchHelpers.TaskAttempt(workId) : 1,
            workflowRunId,
            run);
        var prompts = await _promptResolver.LoadPromptsAsync(workflowRunId);
        if (prompts.Count > 0)
        {
            var promptsMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in prompts)
                promptsMap[p.Key] = p.Body;
            payload["prompts"] = JSON.SerializeToElement(promptsMap);
        }

        return new WorkDispatch(
            WorkflowRunId: workflowRunId,
            WorkId: workId,
            Uses: item.Uses,
            Variables: JSON.Serialize(payload),
            WorkType: workType,
            Stage: item.Stage,
            Title: title);
    }

    /// <summary>
    /// Resolves only the runtime identities needed for admission. This is a
    /// read-only projection: it must not claim the workflow task or persist a
    /// dispatch snapshot. Unknown resolution stays pending so a later poll can
    /// retry with a complete immutable profile.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ResolveRequiredRuntimesAsync(
        WorkItem item,
        WorkflowRun run)
    {
        if (item.IsChecks)
            return (item.Items ?? [])
                .Select(check => RuntimeForUses(check.Uses))
                .Where(runtime => runtime is not null)
                .Select(runtime => runtime!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (string.Equals(item.Uses, "mohist/agent", StringComparison.Ordinal))
        {
            if (item.With is null
                || !item.With.TryGetValue("name", out var name)
                || name is null
                || name.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.Value.GetString()))
                return [];
            if (_agentSnapshots is null || string.IsNullOrWhiteSpace(run.Metadata.ProjectId))
                return null;

            var snapshot = await _agentSnapshots.ResolveAsync(run.Metadata.ProjectId, name.Value.GetString()!);
            return snapshot is null || string.IsNullOrWhiteSpace(snapshot.Runtime)
                ? []
                : [snapshot.Runtime.Trim()];
        }

        var runtime = RuntimeForUses(item.Uses);
        return runtime is null ? [] : [runtime];
    }

    /// <summary>
    /// Returns the capability tuple visible before a workflow claim. Agent
    /// profile tasks use their immutable profile snapshot. Legacy direct
    /// runtime actions may expose model/variant in literal options; their
    /// effort is deliberately unset until the per-launch action contract is
    /// made a first-class snapshot owner.
    /// </summary>
    public async Task<AgentExecutionCapabilityTuple?> ResolveCapabilityTupleAsync(
        WorkItem item,
        WorkflowRun run)
    {
        if (!item.IsTask)
            return null;

        if (string.Equals(item.Uses, "mohist/agent", StringComparison.Ordinal))
        {
            if (item.With is null
                || !item.With.TryGetValue("name", out var name)
                || !name.HasValue
                || name.Value.ValueKind != JsonValueKind.String
                || _agentSnapshots is null
                || string.IsNullOrWhiteSpace(run.Metadata.ProjectId))
                return null;
            var definition = await _agentSnapshots.ResolveAsync(run.Metadata.ProjectId, name.Value.GetString()!);
            return definition is null
                ? null
                : new AgentExecutionCapabilityTuple(
                    definition.Runtime,
                    definition.Model,
                    definition.ReasoningEffort,
                    definition.Variant);
        }

        var runtime = RuntimeForUses(item.Uses);
        if (runtime is null)
            return null;

        // Non-Agent actions keep their model and true variant in the action
        // options, but they have no Agent-owned effort snapshot. They still
        // use the same claim fence when those options name capabilities; the
        // effort member is intentionally frozen as unset for uniformity.
        var (model, variant) = await ResolveDirectActionOptionsAsync(item, run);
        return new AgentExecutionCapabilityTuple(runtime, model, null, variant);
    }

    private async Task<(string? Model, string? Variant)> ResolveDirectActionOptionsAsync(
        WorkItem item,
        WorkflowRun run)
    {
        if (item.With is null || !item.With.TryGetValue("options", out var raw) || raw is null)
            return (null, null);

        var options = raw.Value.ValueKind == JsonValueKind.Object
            ? raw.Value
            : raw.Value.ValueKind == JsonValueKind.String
                && string.Equals(raw.Value.GetString()?.Trim(), "${{ vars.agent }}", StringComparison.Ordinal)
                ? (await _variableResolver.ResolveEffectiveVariableBundleAsync(run.Id, item.Stage)).ResolveStageVars(item.Stage) is { } vars
                    && vars.ValueKind == JsonValueKind.Object
                    && vars.TryGetProperty("agent", out var agent)
                    && agent.ValueKind == JsonValueKind.Object
                    ? agent
                    : (JsonElement?)null
                : null;
        if (options is not { ValueKind: JsonValueKind.Object })
            return (null, null);

        return (
            ReadString(options.Value, "model"),
            ReadString(options.Value, "variant"));
    }

    private static string? ReadString(JsonElement value, string property)
    {
        return value.TryGetProperty(property, out var candidate)
            && candidate.ValueKind == JsonValueKind.String
            ? candidate.GetString()
            : null;
    }

    public async Task<WorkflowAgentHandoffCommand> BuildAgentHandoffCommandAsync(
        WorkItem item,
        string workflowRunId,
        WorkflowRun run)
    {
        if (!item.IsTask || !string.Equals(item.Uses, "mohist/agent", StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow Agent handoff requires a mohist/agent task.");
        var workId = item.Id ?? throw new InvalidOperationException("Workflow Agent task is missing work id.");
        var attempt = run.Stages.SelectMany(stage => stage.Tasks)
            .Single(candidate => string.Equals(candidate.WorkId, workId, StringComparison.Ordinal)
                || string.Equals(candidate.Id, workId, StringComparison.Ordinal));
        var with = item.With ?? throw new WorkflowDispatchRejectedException(
            $"Workflow task '{workId}' has invalid mohist/agent input.",
            new ExecutionError("invalid_agent_input", "mohist/agent requires name and prompt."));
        if (!with.TryGetValue("name", out var nameElement)
            || !nameElement.HasValue
            || nameElement.Value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.Value.GetString())
            || !with.TryGetValue("prompt", out var promptElement)
            || !promptElement.HasValue
            || promptElement.Value.ValueKind != JsonValueKind.String)
        {
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' has invalid mohist/agent input.",
                new ExecutionError("invalid_agent_input", "mohist/agent requires literal name and string prompt."));
        }

        var payload = await BuildPayloadAsync(
            item.Stage,
            workId,
            "task",
            item.Title ?? string.Empty,
            WorkflowDispatchHelpers.TaskAttempt(workId),
            workflowRunId,
            run);
        var prompts = await _promptResolver.LoadPromptsAsync(workflowRunId);
        if (prompts.Count > 0)
            payload["prompts"] = JSON.SerializeToElement(prompts.ToDictionary(prompt => prompt.Key, prompt => prompt.Body, StringComparer.Ordinal));
        var variables = JSON.SerializeToElement(payload);
        var engine = new PromptTemplateEngine();
        var renderedPrompt = engine.Render(promptElement.Value.GetString()!, variables);
        if (renderedPrompt.Errors.Count > 0)
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' prompt could not be rendered.",
                new ExecutionError("invalid_agent_input", string.Join("; ", renderedPrompt.Errors.Select(error => error.Message))));

        string? session = null;
        if (with.TryGetValue("session", out var sessionElement)
            && sessionElement.HasValue
            && sessionElement.Value.ValueKind == JsonValueKind.String)
        {
            var renderedSession = engine.Render(sessionElement.Value.GetString()!, variables);
            if (renderedSession.Errors.Count > 0)
                throw new WorkflowDispatchRejectedException(
                    $"Workflow task '{workId}' session could not be rendered.",
                    new ExecutionError("invalid_agent_input", string.Join("; ", renderedSession.Errors.Select(error => error.Message))));
            session = renderedSession.Rendered;
        }
        long? timeout = null;
        if (with.TryGetValue("timeout", out var timeoutElement)
            && timeoutElement.HasValue
            && timeoutElement.Value.ValueKind == JsonValueKind.Number
            && timeoutElement.Value.TryGetInt64(out var timeoutValue))
            timeout = timeoutValue;

        var repositorySnapshot = run.Repository is { } repository
            ? new[] { new WorkspaceRepositorySnapshot(repository.Name, repository.GitUrl, repository.BaseBranch) }
            : null;
        var workspace = run.Workspace is { } identity
            ? ReadIssueNumber(run) is { } issueNumber
                ? new WorkflowAgentHandoffWorkspace(Name: $"issue-{issueNumber}", Repositories: repositorySnapshot)
                : new WorkflowAgentHandoffWorkspace(Identity: identity, Repositories: repositorySnapshot)
            : null;
        string? reuseSessionId = null;
        if (!string.IsNullOrWhiteSpace(session))
        {
            var previous = run.Stages.SelectMany(stage => stage.Tasks)
                .FirstOrDefault(candidate => candidate.Id != attempt.Id
                    && !string.IsNullOrWhiteSpace(candidate.AgentSessionId)
                    && string.Equals(WorkflowActionAttemptExtensions.ExtractSessionName(candidate.WithInput), session, StringComparison.Ordinal));
            if (previous is not null)
            {
                var previousName = previous.WithInput is not null
                    && previous.WithInput.TryGetValue("name", out var previousNameValue)
                    && previousNameValue.HasValue
                    && previousNameValue.Value.ValueKind == JsonValueKind.String
                        ? previousNameValue.Value.GetString()
                        : null;
                if (!string.Equals(previousName, nameElement.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowDispatchRejectedException(
                        $"Workflow session '{session}' is already bound to Agent '{previousName}'.",
                        new ExecutionError("workflow_session_agent_conflict", "A named Workflow Session cannot switch Agents."));
                reuseSessionId = previous.AgentSessionId;
            }
        }
        return new WorkflowAgentHandoffCommand(
            CommandId: workId,
            ProjectId: run.Metadata.ProjectId
                ?? throw new InvalidOperationException("Workflow Agent task requires Project identity."),
            WorkflowRunId: workflowRunId,
            ActionAttemptId: attempt.Id,
            AgentRef: nameElement.Value.GetString()!,
            Prompt: renderedPrompt.Rendered,
            Session: session,
            TimeoutMilliseconds: timeout,
            Completion: new WorkflowAgentHandoffCompletionSnapshot(
                WorkId: workId,
                Stage: item.Stage,
                Workspace: workspace,
                ExpectJson: SerializeRaw(item.Expect),
                Artifacts: item.Artifacts,
                SetVars: item.SetVars,
                Recovery: item.Recovery,
                RecoveryRemaining: item.RecoveryRemaining,
                IssueNumber: ReadIssueNumber(run),
                EpicNumber: ReadEpicNumber(run),
                VariablesJson: JSON.Serialize(payload)),
            ReuseSessionId: reuseSessionId);
    }

    private async Task<WorkDispatch> BuildTaskDispatchAsync(
        WorkItem item,
        string workflowRunId,
        WorkflowRun run,
        string runnerId)
    {
        var workId = item.Id ?? throw new InvalidOperationException(
            $"Task work item for workflow '{workflowRunId}' is missing work id");
        if (item.Uses is "mohist/opencode" or "mohist/pi")
            throw new WorkflowDispatchRejectedException(
                $"Workflow task '{workId}' uses removed Agent Action '{item.Uses}'.",
                new ExecutionError("removed_agent_action", "Use mohist/agent with a named Agent."));
        var task = run.Stages
            .SelectMany(stage => stage.Tasks)
            .SingleOrDefault(candidate => candidate.Status == WorkflowActionAttemptStatus.Running
                && string.Equals(candidate.WorkId, workId, StringComparison.Ordinal)
                && string.Equals(candidate.WorkerId, runnerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Task work item '{workId}' for workflow '{workflowRunId}' has no running task attempt");
        var actionAttemptId = task.Id;
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
        if (string.Equals(item.Uses, "mohist/agent", StringComparison.Ordinal))
            throw new InvalidOperationException("mohist/agent tasks must enter the AgentJob launch boundary before Runner dispatch.");
        var with = item.With;
        var uses = item.Uses;
        var withStr = SerializeRaw(with);
        var expectStr = SerializeRaw(item.Expect);

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
            AgentDefinition: null,
            ActionAttemptId: actionAttemptId);
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
        payload["workflow"] = JSON.SerializeToElement(new
        {
            runId = workflowRunId,
            verification = run.VerificationCommand is { } command
                ? new { command }
                : null,
        });
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
            ? JSON.SerializeToElement(new
            {
                name = $"issue-{issueNumber}",
                branch = $"mohist/ws-issue-{issueNumber}",
            })
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

    private static string? RuntimeForUses(string? uses) => uses switch
    {
        "mohist/pi" => "pi",
        "mohist/opencode" => "opencode",
        _ => null,
    };

    private static string? SerializeRaw(Dictionary<string, JsonElement?>? values) =>
        values is not null && values.Count > 0 ? JSON.Serialize(values) : null;

    private static int? ReadIssueNumber(WorkflowRun run) =>
        run.Metadata.IssueNumber is > 0 ? run.Metadata.IssueNumber : null;

    private static int? ReadEpicNumber(WorkflowRun run) =>
        run.Metadata.EpicNumber is > 0 ? run.Metadata.EpicNumber : null;

    private static WorkflowActionAttempt? FindFailedTask(WorkflowRun run, string taskId)
    {
        foreach (var stage in run.Stages)
        {
            var task = stage.Tasks.FirstOrDefault(t => t.Id == taskId || t.WorkId == taskId);
            if (task is not null
                && (task.Output.HasValue
                    || task.Error is not null
                    || task.Status == WorkflowActionAttemptStatus.Failed))
            {
                return task;
            }
        }
        return null;
    }

    public abstract record InboundReport
    {
        public sealed record Task(TaskReport Value) : InboundReport;
        public sealed record Checks(CheckReport Value) : InboundReport;
    }

    public InboundReport TranslateResult(
        WorkItem item,
        WorkResult result,
        string workflowRunId)
    {
        if (item.IsTask)
        {
            if (!WorkReportStatus.IsWork(result.Status))
                throw new ArgumentException("Status is invalid for task work.", nameof(result));
            return TranslateTaskResult(item, result, workflowRunId);
        }
        if (item.IsChecks)
        {
            if (!WorkReportStatus.IsChecks(result.Status))
                throw new ArgumentException("Status is invalid for checks work.", nameof(result));
            return TranslateChecksResult(item, result);
        }
        throw new InvalidOperationException(
            $"Unsupported work item variant '{item.WorkType}' for workflow '{workflowRunId}'");
    }

    private static InboundReport TranslateTaskResult(
        WorkItem item,
        WorkResult result,
        string workflowRunId)
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
                Error: new ExecutionError("unexpected-error", shapeError),
                ArtifactUploadIds: result.ArtifactUploadIds is { Length: > 0 }
                    ? result.ArtifactUploadIds.ToArray()
                    : null,
                TerminalResultFingerprint: Mohist.Server.Runner.Grains.WorkResultFingerprint.For(result)));
        }

        return new InboundReport.Task(new TaskReport(
            WorkId: workId,
            Status: status,
            Output: validatedOutput,
            Artifacts: null,
            Detail: detail,
            AddTasks: result.AddTasks is { Count: > 0 } ? result.AddTasks.ToList() : null,
            Error: result.Error,
            ArtifactUploadIds: result.ArtifactUploadIds is { Length: > 0 }
                ? result.ArtifactUploadIds.ToArray()
                : null,
            TerminalResultFingerprint: Mohist.Server.Runner.Grains.WorkResultFingerprint.For(result)));
    }

    private static InboundReport TranslateChecksResult(WorkItem item, WorkResult result)
    {
        if (!HasValidCheckResultRows(item.Items, result.Output))
            return MalformedCheckOutput(item, result);

        var results = WorkflowDispatchHelpers.ParseCheckResults(result.Output);
        return new InboundReport.Checks(new CheckReport(
            item.Stage,
            results,
            WorkResultFingerprint.For(result)));
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

            if (!row.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || !WorkReportStatus.IsChecks(status.GetString()))
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

    private static InboundReport MalformedCheckOutput(WorkItem item, WorkResult result)
    {
        const string message = "Runner reported an invalid check output shape. Check output must be a JSON array of named rows with object-or-null Action output.";
        var error = new ExecutionError("unexpected-error", message);
        var failed = (item.Items ?? [])
            .Select(check => new CheckResult(check.Name, CheckResultStatus.Failed, message, Error: error))
            .ToList();
        return new InboundReport.Checks(new CheckReport(
            item.Stage,
            failed,
            WorkResultFingerprint.For(result)));
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
        WorkReportStatus.IsCompleted(result.Status)
            ? TaskReportStatus.Succeeded
            : TaskReportStatus.Failed;

    private static string? NormalizeDetail(WorkResult result, TaskReportStatus status)
    {
        if (status == TaskReportStatus.Succeeded) return null;
        if (result.Error is not null) return result.Error.Message;
        if (!string.IsNullOrWhiteSpace(result.Message)) return result.Message;
        return result.Status;
    }

}
