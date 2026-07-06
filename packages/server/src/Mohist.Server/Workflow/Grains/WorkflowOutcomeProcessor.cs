using System.Text.Json;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Command-side processor for task / check outcomes. Applies a runner
/// outcome to the in-memory <see cref="WorkflowRun"/>, resolves check repair
/// tasks, schedules a check repair when the failure reason is
/// <see cref="FailureReason.CheckUnrepaired"/>, clears the executable state
/// on stop / retry, marks task / checks running on dispatch, and constructs
/// the <see cref="WorkItem"/> payloads returned to the runner.
///
/// Composed inside the grain process (mirrors <see cref="WorkflowReadModel"/>
/// and the T-001 <see cref="WorkflowStageLockCoordinator"/>): the grain is
/// the consistency boundary for <see cref="WorkflowRun"/> and the outcome
/// path must observe grain state with the same strong-consistency guarantee
/// as the rest of the command surface. Per design D2 the public methods
/// take the mutable <see cref="WorkflowRun"/> by reference — the processor
/// mutates the passed-in run, and the grain's <c>_run</c> field reflects
/// the writes immediately because both point to the same instance.
///
/// Saving and event publishing stay on the grain (design D3):
/// <list type="bullet">
///   <item>Mid-path commit is delivered through a <c>commitAsync</c>
///   callback of signature <c>Func&lt;IReadOnlyList&lt;WorkflowEvent&gt;,
///   Task&gt;</c> — the same shape <see cref="Services.WorkflowSessionHealthService"/>
///   uses for its session-health gate.</item>
///   <item>The two save sites (<c>MarkTaskRunningAsync</c>'s
///   <c>SaveRunAsync(events)</c> after <c>StartTask</c> and
///   <c>ClearExecutableStateAsync</c>'s save with or without
///   <c>FailTaskForStopped</c> events) flow back to the grain's own
///   <c>SaveRunAsync</c> overloads via internal accessors so the ETag
///   conflict → <c>DeactivateOnIdle()</c> reload path stays on the grain.</item>
///   <item>The non-persisted <c>_lastKnownRunnerId</c> cache is written
///   via an internal setter on the grain — the processor never holds a
///   reference to grain infrastructure state.</item>
/// </list>
///
/// The <see cref="ClearExecutableStateAsync"/> path delegates the
/// "release current stage lock" step to the T-001
/// <see cref="WorkflowStageLockCoordinator"/> so all lock-touching logic
/// stays in one composed service.
///
/// No new async yield points are introduced beyond what the extracted
/// methods already used: the awaits in this file are the pre-existing
/// ones (session-health gate round trip, profile-manager stage spec load,
/// grain-owned save path). The run mutations and the save call stay in a
/// straight-line sequence inside each method — there is no new
/// <c>await</c> between mutating <c>run</c> and saving it.
/// </summary>
public sealed class WorkflowOutcomeProcessor
{
    private readonly WorkflowGrain _owner;

    public WorkflowOutcomeProcessor(WorkflowGrain owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Applies a runner-reported <see cref="TaskOutcome"/> to the run.
    /// Mutates the passed run: writes <c>currentTask.Output</c>, resolves
    /// the feedback that caused the task when present, completes the task,
    /// appends recovery tasks from <c>outcome.AddTasks</c>, or — for a
    /// non-Passed outcome — fails the task with the runner's detail.
    ///
    /// Artifact references are surfaced as <see cref="WorkflowArtifactRecorded"/>
    /// events before the task completion event so the run history sees
    /// the artifact before the producing task transitions to Completed.
    /// </summary>
    /// <param name="run">The grain's current run, mutated in place.</param>
    /// <param name="outcome">The runner-reported task outcome.</param>
    /// <param name="taskRunId">The task run id within the current stage.</param>
    /// <param name="workId">The work id the runner reported under.</param>
    public async Task<IReadOnlyList<WorkflowEvent>> ProcessTaskOutcomeAsync(
        WorkflowRun run, TaskOutcome outcome, string taskRunId, string workId)
    {
        var currentStage = run.CurrentStage();
        var currentTask = currentStage?.Tasks.FirstOrDefault(t => t.Id == taskRunId);
        var events = new List<WorkflowEvent>();

        if (outcome.Artifacts is { Count: > 0 })
        {
            // The translator already bound artifact uploads; surface each
            // reference as a recorded event so the run history sees it.
            foreach (var a in outcome.Artifacts)
            {
                events.Add(new WorkflowArtifactRecorded(_owner.GrainKey, taskRunId, a.Path, DateTimeOffset.UtcNow));
            }
        }

        if (outcome.Status == OutcomeStatus.Passed)
        {
            if (currentTask is not null)
                currentTask.Output = ParseOutputToJsonElement(outcome.Output);
            if (currentTask?.CausedByFeedbackId is { } feedbackId)
            {
                var resolved = run.ResolveFeedback(feedbackId, currentTask.Id, outcome.Output);
                if (resolved is not null)
                {
                    _owner.Log.LogInformation(
                        "Workflow {Id} resolved feedback {FeedbackId} via task {TaskId}",
                        _owner.GrainKey, feedbackId, currentTask.Id);
                }
            }
            events.AddRange(run.CompleteTask());

            if (outcome.AddTasks is { Count: > 0 } addTasks)
            {
                var current = run.CurrentStage();
                var taskDefs = addTasks.Select(t =>
                {
                    var with = WorkflowDispatchHelpers.ParseWith(t.With);
                    return new TaskDefinition(t.Id, t.Title, t.Uses, with, Recovery: t.Recovery);
                }).ToList();
                var recoveryEvents = run.AddRuntimeTasks(taskDefs);
                events.AddRange(recoveryEvents);
                _owner.Log.LogInformation(
                    "Workflow {Id} task {TaskId} produced {Count} recovery tasks",
                    _owner.GrainKey, taskRunId, addTasks.Count);
            }
        }
        else
        {
            if (currentTask is not null) currentTask.Output = ParseOutputToJsonElement(outcome.Output);
            var taskResult = new TaskResult("failed", outcome.Detail ?? outcome.Output);
            events.AddRange(run.FailTask(taskResult));
        }

        return events;
    }

    /// <summary>
    /// Adjudicates each <see cref="CheckResult"/> in the runner's
    /// <see cref="CheckOutcome"/> and applies the resulting actions via
    /// <see cref="WorkflowRunExtensions.ProcessCheckResults"/>. Mutates the
    /// passed run by way of <see cref="WorkflowRunExtensions.ProcessCheckResults"/>.
    ///
    /// Per-check semantics: <c>pass</c> → pass action, <c>pending</c> →
    /// pending action, otherwise → try a repair action via
    /// <see cref="ResolveRepairTasks"/>; if a repair is generated the
    /// outcome loop breaks and the repair action is the final entry of
    /// <c>actions</c>. A failure without a repair produces a fail action.
    /// </summary>
    /// <param name="run">The grain's current run, mutated in place.</param>
    /// <param name="outcome">The runner-reported check outcome.</param>
    public async Task<IReadOnlyList<WorkflowEvent>> ProcessCheckOutcomeAsync(WorkflowRun run, CheckOutcome outcome)
    {
        var stage = run.CurrentStageId!;
        var stageDef = await _owner.ProfileManager.LoadStageSpecsAsync(_owner.GrainKey, stage);
        var actions = new List<CheckResultAction>(outcome.Results.Count);

        foreach (var cr in outcome.Results)
        {
            if (cr.Status == "pass")
            {
                actions.Add(new(cr, "pass"));
            }
            else if (cr.Status == "pending")
            {
                actions.Add(new(cr, "pending"));
            }
            else
            {
                var repairTasks = ResolveRepairTasks(run, stageDef, cr.Name, cr);
                actions.Add(repairTasks is not null
                    ? new(cr, "repair", repairTasks)
                    : new(cr, "fail"));
                if (repairTasks is not null)
                    break;
            }
        }

        return run.ProcessCheckResults(actions);
    }

    /// <summary>
    /// Resolves the <see cref="CheckFailureRepair"/> declared on a failed
    /// check, returning the corresponding <see cref="TaskDefinition"/> list
    /// (or <c>null</c> when the check declares no repair, the repair limit
    /// is reached, or the run cannot build a repair task for the current
    /// failure counter).
    /// </summary>
    /// <param name="run">The grain's current run; read for repair counter when <paramref name="enforceLimit"/> is true.</param>
    /// <param name="stageDef">The current stage spec.</param>
    /// <param name="checkName">The name of the failing check.</param>
    /// <param name="result">The runner-reported result, if available.</param>
    /// <param name="enforceLimit">When true, return null once <c>run.GetRepairCount(checkName) >= repair.Limit</c>.</param>
    public IReadOnlyList<TaskDefinition>? ResolveRepairTasks(
        WorkflowRun run,
        StageDefinition? stageDef,
        string checkName,
        CheckResult? result = null,
        bool enforceLimit = true)
    {
        var checkDef = stageDef?.Checks.Find(c => c.Name == checkName);
        if (checkDef?.OnFailure?.Repair is not { } repair) return null;

        if (enforceLimit)
        {
            var repairCount = run.GetRepairCount(checkName);
            if (repairCount >= repair.Limit) return null;
        }

        return run.BuildRepairTasks(checkName, repair, result);
    }

    /// <summary>
    /// Retry-path helper: when the run is Failed with
    /// <see cref="FailureReason.CheckUnrepaired"/> on a known check, builds
    /// the repair tasks (limit not enforced — the retry path is the only
    /// way back from <c>CheckUnrepaired</c>), clears the current stage's
    /// checks running state, and calls <see cref="WorkflowRunExtensions.ScheduleCheckRepair"/>.
    /// Returns <c>null</c> when the run is not in the
    /// <c>CheckUnrepaired</c> shape or no repair can be resolved.
    /// </summary>
    /// <param name="run">The grain's current run, mutated in place.</param>
    public async Task<IReadOnlyList<WorkflowEvent>?> TryScheduleRequestedCheckRepairAsync(WorkflowRun run)
    {
        if (run.Status != WorkflowRunStatus.Failed)
            return null;

        var failure = run.Failure;
        if (failure?.Reason != FailureReason.CheckUnrepaired || string.IsNullOrWhiteSpace(failure.CheckName))
            return null;

        var stageDef = await _owner.ProfileManager.LoadStageSpecsAsync(_owner.GrainKey, failure.Stage);
        var repairTasks = ResolveRepairTasks(run, stageDef, failure.CheckName, enforceLimit: false);
        if (repairTasks is null)
            return null;

        ResetChecksRunningState(run);
        return run.ScheduleCheckRepair(failure.CheckName, repairTasks, failure.Message);
    }

    /// <summary>
    /// Releases the current stage lock, clears the current stage's checks
    /// running state, and — when a task is currently in flight — fails
    /// that task with <see cref="WorkflowRunExtensions.FailTaskForStopped"/>.
    /// The save path goes back to the grain so ETag conflict still triggers
    /// the grain's <c>DeactivateOnIdle()</c> reload.
    ///
    /// Two save shapes are preserved verbatim: with the
    /// <c>FailTaskForStopped</c> events when a task was running (these
    /// events are also published via the bus by the store), or without
    /// events when no task was running.
    /// </summary>
    /// <param name="run">The grain's current run, mutated in place.</param>
    /// <param name="reason">The reason surfaced to the task failure event.</param>
    public async Task ClearExecutableStateAsync(WorkflowRun run, string reason)
    {
        await _owner.ReleaseCurrentStageLocks(reason);

        var currentStage = run.CurrentStage();
        if (currentStage is not null)
        {
            ResetChecksRunningState(run);
        }

        var runningTask = run.CurrentStage().RunningTask;
        if (runningTask is not null)
        {
            var events = run.FailTaskForStopped(reason);
            await _owner.SaveAsyncWithEvents(events);
            return;
        }

        await _owner.SaveAsync();
    }

    /// <summary>
    /// Dispatches a logical task: runs the session-health gate, returns
    /// the existing work id if the task is already running, otherwise
    /// calls <see cref="WorkflowRunExtensions.StartTask"/> and persists
    /// the resulting events. The non-persisted runner id cache on the
    /// grain is updated via the grain's internal setter, and the events
    /// are dispatched to the grain's <c>On()</c> hook so any grain-side
    /// reactions observe the new <c>TaskStarted</c>.
    /// </summary>
    /// <param name="run">The grain's current run, mutated in place when starting.</param>
    /// <param name="logicalTaskId">The logical task id within the current stage.</param>
    /// <param name="runnerId">The runner claiming the task.</param>
    /// <param name="commitAsync">The grain's <c>CommitAsync</c> callback used by the session-health gate.</param>
    public async Task<string?> MarkTaskRunningAsync(
        WorkflowRun run,
        string logicalTaskId,
        string runnerId,
        Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync)
    {
        var current = run.CurrentStage();
        await _owner.SessionHealthGate.CheckAndEnforceAsync(
            logicalTaskId, current.Id, _owner.GrainKey, run,
            commitAsync, "dispatch", default);

        var currentTask = current.Tasks.FirstOrDefault(t => t.Id == logicalTaskId);
        if (currentTask?.Status == TaskRunStatus.Running)
        {
            _owner.SetLastKnownRunnerId(runnerId);
            return currentTask.WorkId ?? logicalTaskId;
        }

        var workId = logicalTaskId;
        var events = run.StartTask(workId, runnerId);
        await _owner.SaveAsyncWithEvents(events);
        foreach (var e in events)
            await _owner.DispatchEvent(e);

        _owner.SetLastKnownRunnerId(runnerId);
        return workId;
    }

    /// <summary>
    /// Writes the deterministic checks work id
    /// (<see cref="ChecksWorkIdFor"/>) to the current stage's
    /// <c>ChecksWorkId</c>, marks each matching check Running with
    /// <c>StartedAt</c> = now, and flips the run status to Running. Called
    /// by the claim path (<see cref="ClaimWorkItemAsync"/>) once a runner
    /// has durably registered the work.
    /// </summary>
    /// <param name="run">The grain's current run, mutated in place.</param>
    /// <param name="stage">The current stage id.</param>
    /// <param name="items">The pending check items to dispatch.</param>
    public string MarkChecksRunning(WorkflowRun run, string stage, IReadOnlyList<CheckItem> items)
    {
        var checksWorkId = ChecksWorkIdFor(stage);
        var currentStage = run.CurrentStage();
        currentStage.ChecksWorkId = checksWorkId;
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var check = currentStage.Checks.FirstOrDefault(c => c.Name == item.Name);
            if (check is not null)
            {
                check.Status = StageCheckStatus.Running;
                check.StartedAt = now;
            }
        }
        run.Status = WorkflowRunStatus.Running;
        return checksWorkId;
    }

    /// <summary>
    /// Builds a <see cref="WorkItem"/> for an offered work WITHOUT mutating
    /// the run state. This is the offer-phase projection: it produces the
    /// work item the runner takes away so it can durably claim it, but
    /// leaves the task/check Pending and the run untouched. The state
    /// transition to Running happens only when the runner calls back via
    /// <c>ClaimAsync</c> (<see cref="ClaimWorkItemAsync"/>).
    ///
    /// The work id is deterministic so the same offer yields the same id
    /// across calls: a task's id is its <see cref="TaskRun.Id"/>; a checks
    /// batch's id is <c>checks-{stage}</c> (a stage has at most one
    /// outstanding checks batch at a time — see <c>NextWork</c>).
    /// </summary>
    public WorkItem? BuildWorkItem(WorkflowRun run, WorkflowWork work)
    {
        switch (work.WorkType)
        {
            case "task":
            {
                var t = (WorkflowWork.TaskData)work.Data;
                return WorkItem.Task(
                    stage: work.Stage,
                    id: t.Id,
                    title: t.Title,
                    uses: t.Uses,
                    with: t.With,
                    artifacts: t.Artifacts,
                    setVars: t.SetVars,
                    recovery: t.Recovery);
            }
            case "checks":
            {
                var ch = (WorkflowWork.ChecksData)work.Data;
                return WorkItem.Checks(work.Stage, ChecksWorkIdFor(work.Stage), ch.Items);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Claims an offered work item on behalf of a runner that has durably
    /// registered it. This is the claim-phase counterpart of
    /// <see cref="BuildWorkItem"/>: it performs the state transition to
    /// Running (task via <see cref="WorkflowRunExtensions.StartTask"/>,
    /// checks batch via <see cref="MarkChecksRunning"/>), persists it, and
    /// returns the resolved work id the runner should use for reporting.
    ///
    /// Idempotent: a claim for an already-Running work item succeeds and
    /// returns the in-flight work id. Returns <c>null</c> when the work id
    /// no longer maps to offerable work in the current stage (the offer was
    /// overtaken — e.g. another runner claimed it, or the stage advanced).
    /// </summary>
    public async Task<string?> ClaimWorkItemAsync(
        WorkflowRun run,
        string workId,
        string runnerId,
        Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync)
    {
        var currentStage = run.CurrentStage();

        // Task claim: the offered work id equals the pending task's id.
        var task = currentStage.Tasks.FirstOrDefault(t => t.Id == workId);
        if (task is not null)
        {
            if (task.Status == TaskRunStatus.Running)
            {
                _owner.SetLastKnownRunnerId(runnerId);
                return task.WorkId ?? task.Id;
            }
            if (task.Status != TaskRunStatus.Pending) return null;

            var claimedWorkId = await MarkTaskRunningAsync(run, task.Id, runnerId, commitAsync);
            return claimedWorkId;
        }

        // Checks claim: the offered work id is the deterministic checks id.
        if (workId == ChecksWorkIdFor(currentStage.Id))
        {
            // If a batch is already running for this stage, claim is a no-op.
            if (!string.IsNullOrWhiteSpace(currentStage.ChecksWorkId))
            {
                _owner.SetLastKnownRunnerId(runnerId);
                return currentStage.ChecksWorkId;
            }

            var items = currentStage.Checks
                .Where(c => c.Status == StageCheckStatus.Pending)
                .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                .ToList();
            if (items.Count == 0) return null;

            var checksWorkId = MarkChecksRunning(run, currentStage.Id, items);
            // MarkChecksRunning only mutates in-memory state; persist the
            // claimed checks batch (task claim persists itself via
            // MarkTaskRunningAsync). Empty events: checks claim emits no
            // workflow events, but the run state must be saved so the
            // Running status and ChecksWorkId survive grain deactivation.
            await commitAsync([]);
            return checksWorkId;
        }

        return null;
    }

    /// <summary>
    /// Deterministic work id for a checks batch on the given stage. A stage
    /// has at most one outstanding checks batch at a time (tasks always
    /// precede checks in <c>NextWork</c>), so the stage id alone identifies
    /// the batch. This keeps the id stable across re-offers without mutable
    /// staging state on the grain (which is [Reentrant]).
    /// </summary>
    public static string ChecksWorkIdFor(string stage) => $"checks-{stage}";

    private static JsonElement? ParseOutputToJsonElement(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.Clone();
        }
        catch
        {
            var wrapped = JsonSerializer.SerializeToElement(output);
            return wrapped;
        }
    }

    /// <summary>
    /// Resets the current stage's checks running state: nulls
    /// <c>ChecksWorkId</c> and demotes every still-Running check back to
    /// <see cref="StageCheckStatus.Pending"/> with <c>StartedAt</c> cleared.
    /// Used by the report-check-outcome path (after
    /// <see cref="ProcessCheckOutcomeAsync"/>), by
    /// <see cref="ClearExecutableStateAsync"/>, and by
    /// <see cref="TryScheduleRequestedCheckRepairAsync"/>.
    /// </summary>
    public void ResetChecksRunningState(WorkflowRun run)
    {
        var currentStage = run.CurrentStage();
        currentStage.ChecksWorkId = null;
        foreach (var ch in currentStage.Checks.Where(c => c.Status == StageCheckStatus.Running))
        {
            ch.Status = StageCheckStatus.Pending;
            ch.StartedAt = null;
        }
    }
}