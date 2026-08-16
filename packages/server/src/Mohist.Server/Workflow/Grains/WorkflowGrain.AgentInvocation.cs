using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Workflow-owned finalization of a delegated Agent invocation (issue
/// 559, design D7). <see cref="SettleAgentInvocationAsync"/> consumes the
/// AgentJob terminal delivered over the typed workflow-terminal transport
/// and applies the task completion effects exactly once, guarded by a
/// durable per-effect <see cref="AgentInvocationSettlement"/> receipt on
/// the TaskRun: artifact binding through the shared pending-upload
/// service, setVars extraction and application through the same store
/// the runner's patch route uses, then the task outcome through the same
/// domain settlement calls the inline report path uses
/// (<c>ApplyTaskReportAsync</c> — complete/fail, feedback resolution,
/// advancement — with recovery <c>when</c>-matching under the remaining
/// budget for failures). An interrupted settlement resumes from the
/// recorded flags via the finalizer reconcile reminder; duplicate or
/// stale terminals are acknowledged without reapplying effects.
/// Workflow advancement never leaves the Workflow grain; the AgentJob
/// applies no task effect.
/// </summary>
public partial class WorkflowGrain
{
    public const string AgentInvocationSettlementReminderName = "workflow-agent-invocation-settlement";

    /// <summary>
    /// Binds the immutable invocation linkage onto a running task attempt
    /// (the handoff claim-time write, design D9). The linkage is the stop
    /// cascade's handle to the backing AgentJob and the Workflow surface's
    /// queryable identity; binding also removes the inline-style
    /// agent-result settlement a <c>mohist/agent</c> claim created, since
    /// a delegated invocation's authoritative channel is the terminal
    /// transport, not a Runner task report.
    /// </summary>
    public async Task<ReportAck> BindAgentInvocationAsync(AgentInvocationLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

        var update = _run.BindAgentInvocation(link);
        if (update == AgentExecutionUpdate.Rejected) return ReportAck.Stale;
        if (update == AgentExecutionUpdate.Updated)
        {
            _log.LogInformation(
                "Workflow {Id} bound Agent invocation {Invocation} to task {TaskRun} (job={JobId})",
                GrainKey, link.InvocationId, link.TaskRunId, link.JobId);
            await CommitAsync([]);
        }

        return ReportAck.Accepted;
    }

    public async Task<AgentInvocationSettlementAck> SettleAgentInvocationAsync(AgentInvocationTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        RejectIfRunReloadRequired();
        if (_run is null
            || string.IsNullOrWhiteSpace(terminal.DeliveryId)
            || !string.Equals(terminal.WorkflowRunId, GrainKey, StringComparison.Ordinal))
        {
            return AgentInvocationSettlementAck.Stale;
        }

        var match = _run.FindAgentInvocationTask(
            terminal.TaskRunId,
            terminal.WorkId,
            terminal.InvocationId,
            terminal.JobId);
        if (match is null)
        {
            // Unknown attempt for this run: acknowledge without effects.
            _log.LogDebug(
                "Workflow {Id} ignored Agent invocation terminal {Delivery} for unknown attempt {TaskRun}/{Work}",
                GrainKey, terminal.DeliveryId, terminal.TaskRunId, terminal.WorkId);
            return AgentInvocationSettlementAck.Stale;
        }

        var task = match.Task;
        if (!MatchesAgentInvocationTerminal(task.AgentInvocation, terminal))
        {
            _log.LogDebug(
                "Workflow {Id} acknowledged Agent invocation terminal {Delivery} as stale: lineage mismatch for task {TaskRun}",
                GrainKey, terminal.DeliveryId, task.Id);
            return AgentInvocationSettlementAck.Stale;
        }

        if (task.AgentInvocationSettlement is { } receipt)
        {
            if (!string.Equals(receipt.Terminal.DeliveryId, terminal.DeliveryId, StringComparison.Ordinal))
            {
                // The attempt's settlement is owned by a different terminal
                // delivery; the first delivery won.
                _log.LogDebug(
                    "Workflow {Id} ignored Agent invocation terminal {Delivery} for already-owed attempt {TaskRun}",
                    GrainKey, terminal.DeliveryId, task.Id);
                return receipt.IsSettled
                    ? AgentInvocationSettlementAck.AlreadyApplied
                    : AgentInvocationSettlementAck.Stale;
            }

            if (receipt.IsSettled)
                return AgentInvocationSettlementAck.AlreadyApplied;

            // Resume the interrupted settlement from the frozen terminal
            // snapshot and the recorded per-effect flags.
            await ApplyAgentInvocationEffectsAsync(match.Stage, task, receipt);
            return receipt.IsSettled
                ? AgentInvocationSettlementAck.Applied
                : AgentInvocationSettlementAck.Stale;
        }

        // First delivery for this attempt: the attempt must still accept an
        // authoritative result. A stale terminal (task already terminal, run
        // stopped) is acknowledged without applying effects.
        if (_run.FindReportableWork(
                task.Id,
                task.WorkId ?? terminal.WorkId,
                task.WorkerId ?? string.Empty) is null)
        {
            _log.LogDebug(
                "Workflow {Id} acknowledged stale Agent invocation terminal {Delivery} for attempt {TaskRun} (status={Status})",
                GrainKey, terminal.DeliveryId, task.Id, task.Status);
            return AgentInvocationSettlementAck.Stale;
        }

        receipt = new AgentInvocationSettlement
        {
            Terminal = terminal,
            ReceivedAt = Now(),
        };
        task.AgentInvocationSettlement = receipt;
        // Register the reconcile reminder BEFORE the receipt persists: a
        // crash between the two leaves a reminder with nothing pending
        // (removed on the next reconcile), never an unsettled receipt
        // without a resumable owner.
        await EnsureAgentInvocationSettlementReminderAsync();
        await CommitAsync([]);
        await OnAgentInvocationReceiptPersistedAsync(receipt);

        await ApplyAgentInvocationEffectsAsync(match.Stage, task, receipt);
        return receipt.IsSettled
            ? AgentInvocationSettlementAck.Applied
            : AgentInvocationSettlementAck.Stale;
    }

    private async Task ApplyAgentInvocationEffectsAsync(
        string stageId,
        TaskRun task,
        AgentInvocationSettlement receipt)
    {
        var terminal = receipt.Terminal;

        // The attempt may have left the reportable window while the
        // settlement was interrupted (run stopped, task settled elsewhere).
        // Acknowledge the receipt as settled without applying the remaining
        // effects — the outcome was decided by the other writer.
        if (!receipt.OutcomeApplied && task.Status != TaskRunStatus.Running)
        {
            receipt.ArtifactsBound = true;
            receipt.SetVarsApplied = true;
            receipt.OutcomeApplied = true;
            receipt.SettlementApplied = true;
            receipt.AdvancementApplied = true;
            receipt.SettledAt = Now();
            await CommitAsync([]);
            await RemoveAgentInvocationSettlementReminderIfSettledAsync();
            return;
        }

        // Effect 1: artifact binding (shared pending-upload service; rows
        // are idempotent under the upload ids, and the bind outcome is
        // frozen on the receipt so a resume never re-reads it).
        if (!receipt.ArtifactsBound)
        {
            if (terminal.ArtifactUploadIds is { Length: > 0 })
            {
                var bound = await BindAgentInvocationArtifactsAsync(stageId, task, terminal.ArtifactUploadIds);
                if (bound.Error is null)
                {
                    receipt.BoundArtifactPaths = bound.Paths;
                }
                else
                {
                    receipt.BoundArtifactPaths = [];
                    receipt.ArtifactBindError = bound.Error;
                    _log.LogWarning(
                        "Workflow {Id} work {Work} Agent invocation artifact binding failed: {Reason}",
                        GrainKey, task.WorkId, bound.Error);
                }
            }

            receipt.ArtifactsBound = true;
            await CommitAsync([]);
            await OnAgentInvocationArtifactsBoundAsync(receipt);
        }

        // Effect 2: setVars extraction and application (same store the
        // runner's patch route uses). The extraction is deterministic from
        // the frozen terminal, so a resume recomputes it; the applied
        // outcome (vars or failure message) is frozen on the receipt.
        if (!receipt.SetVarsApplied)
        {
            var projection = BuildAgentInvocationReport(task, receipt);
            if (projection.Status == TaskReportStatus.Succeeded && task.SetVars is { Count: > 0 })
            {
                var extraction = ExtractAgentInvocationSetVars(task.SetVars, projection.Output);
                if (extraction.Error is not null)
                {
                    receipt.SetVarsFailure = $"setVars: {extraction.Error}";
                }
                else if (extraction.Vars is { Count: > 0 })
                {
                    try
                    {
                        await PatchAgentInvocationVariablesAsync(extraction.Vars);
                    }
                    catch (Exception ex)
                    {
                        var message = $"setVars patch failed: {ex.Message}";
                        receipt.SetVarsFailure = message.Length > 4000 ? message[..4000] : message;
                    }
                }
            }

            receipt.SetVarsApplied = true;
            await CommitAsync([]);
            await OnAgentInvocationSetVarsAppliedAsync(receipt);
        }

        // Effect 3: the task outcome — the same domain settlement calls the
        // inline report path uses (complete/fail, feedback resolution,
        // recovery decisions, advancement). No-ops when the task already
        // left Running, which keeps a resumed outcome idempotent.
        if (!receipt.OutcomeApplied)
        {
            var report = BuildAgentInvocationReport(task, receipt);
            IReadOnlyList<RuntimeTaskInput>? addTasks = null;
            if (report.Status == TaskReportStatus.Failed && CanApplyAgentRecovery(receipt))
            {
                var prompts = await LoadAgentInvocationPromptsAsync();
                addTasks = AgentInvocationRecovery.TryRecover(task, report, prompts, out var recoveryFailure);
                if (addTasks is not null)
                {
                    var label = string.IsNullOrWhiteSpace(task.Title) ? task.Uses ?? task.Id : task.Title;
                    var schedulingMessage = report.Error?.Message ?? $"{label} recovery scheduled";
                    report = report with
                    {
                        Status = TaskReportStatus.Succeeded,
                        Detail = string.IsNullOrEmpty(report.Detail)
                            ? schedulingMessage
                            : $"{report.Detail}; {schedulingMessage}"[..Math.Min(report.Detail.Length + 2 + schedulingMessage.Length, 4000)],
                        AddTasks = addTasks,
                    };
                }
                else if (recoveryFailure is not null)
                {
                    report = report with
                    {
                        Detail = recoveryFailure,
                        Error = new ExecutionError("recovery-reference-unresolved", recoveryFailure),
                    };
                }
            }

            var events = await _workLifecycle.ApplyTaskReportAsync(_run!, report, stageId, task.Id);
            _log.LogInformation(
                "Workflow {Id} settled Agent invocation {Invocation} for task {TaskRun}: {Status} detail={Detail}",
                GrainKey, receipt.Terminal.InvocationId, task.Id, report.Status, report.Detail ?? "(none)");

            receipt.OutcomeApplied = true;
            receipt.SettlementApplied = true;
            receipt.AdvancementApplied = true;
            receipt.SettledAt = Now();
            await CommitAsync(events);
        }

        await RemoveAgentInvocationSettlementReminderIfSettledAsync();
    }

    /// <summary>
    /// Builds the effective task report from the frozen terminal, mirroring
    /// the inline executor's agent-turn projection
    /// (<c>projectTaskOutput</c>): a completed job with a satisfied (or
    /// absent) expectation succeeds with the promise projection
    /// (<c>{"promise": VALUE}</c> from the matched marker, else null); an
    /// unsatisfied expectation fails with the inline
    /// <c>expectation-failed</c> code and the boundary evaluation's message
    /// while still projecting the matched promise; a failed or cancelled
    /// job follows the failure path with the terminal's reason.
    /// </summary>
    private TaskReport BuildAgentInvocationReport(TaskRun task, AgentInvocationSettlement receipt)
    {
        var terminal = receipt.Terminal;
        var workId = task.WorkId ?? terminal.WorkId;

        if (terminal.Status is AgentInvocationTerminalStatus.Failed or AgentInvocationTerminalStatus.Cancelled)
        {
            var message = terminal.Status == AgentInvocationTerminalStatus.Cancelled
                ? FirstNonEmpty(terminal.Message, terminal.FailureReason, "agent job cancelled")
                : FirstNonEmpty(terminal.Message, terminal.FailureReason, "agent job failed");
            var code = FirstNonEmpty(terminal.FailureCategory, terminal.FailureReason)
                ?? (terminal.Status == AgentInvocationTerminalStatus.Cancelled ? "cancelled" : "agent-job-failed");
            return new TaskReport(
                workId,
                TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: message,
                Error: new ExecutionError(code, message!));
        }

        if (receipt.ArtifactBindError is { } bindError)
        {
            return new TaskReport(
                workId,
                TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: bindError);
        }

        var expectation = terminal.Expectation;
        var output = ProjectAgentInvocationOutput(expectation?.Matched);
        if (receipt.SetVarsFailure is { } setVarsFailure)
        {
            return new TaskReport(
                workId,
                TaskReportStatus.Failed,
                Output: output,
                Artifacts: null,
                Detail: setVarsFailure);
        }

        if (expectation is { Satisfied: false })
        {
            var message = expectation.Message ?? "Workflow completion requirements were not satisfied";
            return new TaskReport(
                workId,
                TaskReportStatus.Failed,
                Output: output,
                Artifacts: BoundArtifacts(receipt),
                Detail: message,
                Error: new ExecutionError("expectation-failed", message));
        }

        return new TaskReport(
            workId,
            TaskReportStatus.Succeeded,
            Output: output,
            Artifacts: BoundArtifacts(receipt));
    }

    private static JsonElement? ProjectAgentInvocationOutput(string? matched)
    {
        var promise = AgentInvocationSettlementExtensions.PromiseValue(matched);
        return promise is null
            ? null
            : JSON.SerializeToElement(new Dictionary<string, string?> { ["promise"] = promise });
    }

    private static IReadOnlyList<ArtifactRef>? BoundArtifacts(AgentInvocationSettlement receipt) =>
        receipt.BoundArtifactPaths is { Length: > 0 }
            ? receipt.BoundArtifactPaths.Select(path => new ArtifactRef(path)).ToList()
            : null;

    private async Task<(string[]? Paths, string? Error)> BindAgentInvocationArtifactsAsync(
        string stageId,
        TaskRun task,
        string[] artifactUploadIds)
    {
        var variables = await _variableResolver.ResolveEffectiveVariableBundleAsync(GrainKey, stageId);
        var bindResult = await _artifactBindService.BindAsync(
            GrainKey,
            task.WorkId!,
            task.Id,
            artifactUploadIds.Distinct(StringComparer.Ordinal).ToArray(),
            task.Artifacts,
            variables.Vars,
            GetProjectId(),
            GetIssueNumber());
        if (!bindResult.IsSuccess)
            return (null, bindResult.Error ?? "artifact binding failed");

        return (bindResult.ArtifactRecordedEvents.Select(recorded => recorded.Path).ToArray(), null);
    }

    /// <summary>
    /// Port of the runner's <c>extractSetVars</c>: resolves each source
    /// path (with the <c>output.</c> prefix stripped) against the settled
    /// output object and projects it onto the target path. Any missing
    /// path is an error with the runner's message.
    /// </summary>
    private static (Dictionary<string, JsonElement?>? Vars, string? Error) ExtractAgentInvocationSetVars(
        Dictionary<string, string> setVars,
        JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object })
        {
            return (null, output is null
                ? "task output is null; cannot project setVars source paths"
                : "task output is not a JSON object");
        }

        var source = output.Value;
        var result = new Dictionary<string, JsonElement?>();
        foreach (var (targetPath, sourcePath) in setVars)
        {
            var resolvedPath = sourcePath.StartsWith("output.", StringComparison.Ordinal)
                ? sourcePath["output.".Length..]
                : sourcePath;
            if (GetJsonPath(source, resolvedPath) is not { } value)
                return (null, $"setVars source path '{sourcePath}' not found in task output");
            SetJsonPath(result, targetPath, value);
        }

        return (result, null);
    }

    private static JsonElement? GetJsonPath(JsonElement obj, string path)
    {
        var current = (JsonElement?)obj;
        foreach (var part in path.Split('.'))
        {
            if (current is not { ValueKind: JsonValueKind.Object }
                || !current.Value.TryGetProperty(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static void SetJsonPath(Dictionary<string, JsonElement?> obj, string path, JsonElement value)
    {
        var segments = path.Split('.');
        obj[segments[0]] = segments.Length == 1
            ? value
            : SerializeNested(
                obj.TryGetValue(segments[0], out var existing) && existing is { ValueKind: JsonValueKind.Object }
                    ? existing.Value
                    : null,
                segments[1..],
                value);
    }

    private static JsonElement SerializeNested(JsonElement? existing, string[] segments, JsonElement value)
    {
        var child = existing is { ValueKind: JsonValueKind.Object }
            ? existing.Value.EnumerateObject().ToDictionary(
                p => p.Name,
                (Func<JsonProperty, JsonElement?>)(p => p.Value.Clone()))
            : [];
        child[segments[0]] = segments.Length == 1
            ? value
            : SerializeNested(
                child.TryGetValue(segments[0], out var next) && next is { ValueKind: JsonValueKind.Object }
                    ? next.Value
                    : null,
                segments[1..],
                value);
        return JSON.SerializeToElement(child);
    }

    private async Task PatchAgentInvocationVariablesAsync(Dictionary<string, JsonElement?> vars)
    {
        var patch = new VariableBundle(Vars: JSON.SerializeToElement(vars));
        var current = await _runVariablesStore.GetVariablesAsync(GrainKey);
        var desired = VariableBundle.Patch(current, patch);

        // The variable store is separate from the WorkflowRun receipt. If a
        // process stops after the patch commits but before SetVarsApplied is
        // persisted, reconciliation must observe the requested values and
        // avoid issuing a second patch.
        if (JsonElement.DeepEquals(current.ToElement(), desired.ToElement()))
            return;

        await _runVariablesStore.PatchVariablesAsync(GrainKey, patch);
    }

    private static bool CanApplyAgentRecovery(AgentInvocationSettlement receipt)
    {
        if (receipt.ArtifactBindError is not null || receipt.SetVarsFailure is not null)
            return false;

        return receipt.Terminal.Status == AgentInvocationTerminalStatus.Failed
            || (receipt.Terminal.Status == AgentInvocationTerminalStatus.Completed
                && receipt.Terminal.Expectation is { Satisfied: false });
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadAgentInvocationPromptsAsync()
    {
        var prompts = await _promptResolver.LoadPromptsAsync(GrainKey);
        return prompts.ToDictionary(prompt => prompt.Key, prompt => prompt.Body, StringComparer.Ordinal);
    }

    private static bool MatchesAgentInvocationTerminal(
        AgentInvocationLink? link,
        AgentInvocationTerminal terminal)
    {
        if (link is null
            || !string.Equals(link.InvocationId, terminal.InvocationId, StringComparison.Ordinal)
            || !string.Equals(link.TaskRunId, terminal.TaskRunId, StringComparison.Ordinal)
            || !string.Equals(link.WorkId, terminal.WorkId, StringComparison.Ordinal)
            || !string.Equals(link.JobId, terminal.JobId, StringComparison.Ordinal))
        {
            return false;
        }

        // Input/turn were added to the finalizer command after the initial
        // receipt shape. Accept an older direct command that omitted them,
        // but validate them whenever the transport carried the identifiers.
        return (terminal.SessionId is null || string.Equals(link.SessionId, terminal.SessionId, StringComparison.Ordinal))
            && (terminal.InputId is null || string.Equals(link.InputId, terminal.InputId, StringComparison.Ordinal))
            && (terminal.TurnId is null || string.Equals(link.TurnId, terminal.TurnId, StringComparison.Ordinal));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Finalizer reconciliation: resumes interrupted settlements from the
    // recorded per-effect flags. Driven by the reconcile reminder and on
    // activation, mirroring the agent-result-settlement reminder pattern.
    // ------------------------------------------------------------------

    private async Task ReconcileAgentInvocationSettlementsAsync()
    {
        if (_run is null)
            return;

        var pending = _run.FindUnsettledAgentInvocationTask();
        if (pending is null)
        {
            await RemoveAgentInvocationSettlementReminderAsync();
            return;
        }

        await EnsureAgentInvocationSettlementReminderAsync();
        await SettleAgentInvocationAsync(pending.Task.AgentInvocationSettlement!.Terminal);
    }

    protected virtual Task EnsureAgentInvocationSettlementReminderAsync()
    {
        var period = _agentInvocationSettlementReconcileInterval;
        return this.RegisterOrUpdateReminder(
            AgentInvocationSettlementReminderName,
            dueTime: period,
            period: period);
    }

    protected virtual async Task RemoveAgentInvocationSettlementReminderAsync()
    {
        try
        {
            var reminder = await this.GetReminder(AgentInvocationSettlementReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (ArgumentNullException ex) when (string.Equals(ex.ParamName, "provider", StringComparison.Ordinal))
        {
            // Direct contract tests can construct a grain without an Orleans
            // reminder registry. There is no reminder to remove in that host.
        }
    }

    private async Task RemoveAgentInvocationSettlementReminderIfSettledAsync()
    {
        if (_run?.FindUnsettledAgentInvocationTask() is null)
            await RemoveAgentInvocationSettlementReminderAsync();
    }

    // Spec seams: invoked after each durable receipt persistence and before
    // the next effect so a throw simulates an interruption between the
    // persisted flags and the remaining effects.
    protected virtual Task OnAgentInvocationReceiptPersistedAsync(AgentInvocationSettlement receipt) => Task.CompletedTask;
    protected virtual Task OnAgentInvocationArtifactsBoundAsync(AgentInvocationSettlement receipt) => Task.CompletedTask;
    protected virtual Task OnAgentInvocationSetVarsAppliedAsync(AgentInvocationSettlement receipt) => Task.CompletedTask;
}
