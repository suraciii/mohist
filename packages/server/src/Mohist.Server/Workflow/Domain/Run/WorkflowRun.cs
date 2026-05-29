using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public class WorkflowRun
{
    private readonly List<StageRun> _stageRuns = [];
    private readonly Dictionary<string, int> _stageAttempts = new();
    private bool _started;
    private bool _paused;

    public string Id { get; }
    public IReadOnlyList<StageRun> StageRuns => _stageRuns.AsReadOnly();
    public StageRun CurrentStage { get; private set; }

    public WorkflowRunStatus Status
    {
        get
        {
            if (!_started) return WorkflowRunStatus.Pending;
            if (CurrentStage.Status == StageRunStatus.Failed) return WorkflowRunStatus.Failed;
            if (_paused) return WorkflowRunStatus.Paused;
            if (CurrentStage.Status == StageRunStatus.AwaitingApproval) return WorkflowRunStatus.AwaitingApproval;
            if (CurrentStage.Order == _stageRuns.Count - 1 && CurrentStage.Status == StageRunStatus.Completed) return WorkflowRunStatus.Completed;
            return WorkflowRunStatus.Running;
        }
    }

    public FailureDetails? Failure => CurrentStage.Failure;

    public IReadOnlyList<string> StageOrder =>
        _stageRuns.Select(s => s.Stage).ToList().AsReadOnly();

    public IReadOnlyList<StageRunState> Stages =>
        _stageRuns.Select(sr => new StageRunState(
            sr.Stage,
            sr.Status,
            sr.Order,
            sr.Tasks.Select(t => new TaskRunState(t.Id, t.Title, t.Uses, t.WithInput, t.Status)).ToList(),
            sr.Checks.Select(c => new CheckRunState(c.Name, c.Title, c.Uses, c.WithInput, c.Status, c.Message, c.Output)).ToList(),
            sr.Approval is not null
                ? new ApprovalState(sr.Approval.Status, sr.Approval.Output, sr.Approval.RequestedAt, sr.Approval.RespondedAt)
                : null,
            sr.Failure
        )).ToList().AsReadOnly();

    public WorkflowRun(string id, WorkflowRunProfile profile)
    {
        var stages = profile.Definition.Stages;
        if (stages.Count == 0)
            throw new WorkflowDomainException("WorkflowRun requires at least one stage definition");

        Id = id;

        for (var i = 0; i < stages.Count; i++)
        {
            var def = stages[i];
            var attempt = _stageAttempts.GetValueOrDefault(def.Stage, 0) + 1;
            _stageAttempts[def.Stage] = attempt;
            _stageRuns.Add(new StageRun(def.Stage, i, attempt, def.RequiresApproval));
        }

        CurrentStage = _stageRuns[0];
    }

    public WorkflowRunSnapshot Snapshot() => new(
        Id,
        _started,
        _paused,
        Math.Max(0, _stageRuns.IndexOf(CurrentStage)),
        new Dictionary<string, int>(_stageAttempts),
        _stageRuns.Select(s => s.Snapshot()).ToList());

    public static WorkflowRun Restore(WorkflowRunProfile profile, WorkflowRunSnapshot snapshot)
    {
        var run = new WorkflowRun(snapshot.Id, profile);
        run._stageRuns.Clear();
        run._stageAttempts.Clear();

        foreach (var (key, value) in snapshot.StageAttempts)
            run._stageAttempts[key] = value;

        foreach (var stageSnapshot in snapshot.Stages)
            run._stageRuns.Add(StageRun.Restore(stageSnapshot));

        if (run._stageRuns.Count == 0)
            throw new WorkflowDomainException("Cannot restore WorkflowRun without stage runs");

        var index = Math.Clamp(snapshot.CurrentStageIndex, 0, run._stageRuns.Count - 1);
        run.CurrentStage = run._stageRuns[index];
        run._started = snapshot.Started;
        run._paused = snapshot.Paused;
        return run;
    }

    public void Start()
    {
        if (Status != WorkflowRunStatus.Pending && Status != WorkflowRunStatus.Paused)
            throw new WorkflowDomainException($"WorkflowRun is {Status}");

        _started = true;
        _paused = false;

        if (CurrentStage.Status == StageRunStatus.Pending)
            CurrentStage.Start();
    }

    public WorkflowWork? GetNextWork()
    {
        var status = Status;
        if (status != WorkflowRunStatus.Running)
            return null;

        if (CurrentStage.Status != StageRunStatus.Running)
            return null;

        var work = CurrentStage.GetNextWork();
        if (work is null) return null;

        return MapWork(work);
    }

    public void Pause()
    {
        if (Status != WorkflowRunStatus.Running) return;
        _paused = true;
    }

    public void InitStage(IReadOnlyList<LoadedTaskInput> tasks, List<CheckDefinition> checks)
    {
        CurrentStage.Init(tasks, checks);
        Advance();
    }

    public void FailStage(string reason) =>
        CurrentStage.Failure = new FailureDetails(FailureReason.TaskFailed, CurrentStage.Stage, Message: reason);

    public void FailInFlightWork(string workType, string? reason)
    {
        switch (workType)
        {
            case "task":
                CurrentStage.FailInFlightTask(reason);
                break;
            case "load":
                CurrentStage.Failure = new FailureDetails(FailureReason.TaskFailed, CurrentStage.Stage, Message: reason ?? "Task loading failed");
                break;
            case "check":
            case "checks":
                CurrentStage.FailPendingChecks(reason);
                break;
            default:
                CurrentStage.Failure = new FailureDetails(FailureReason.TaskFailed, CurrentStage.Stage, Message: reason ?? $"In-flight work lost (type={workType})");
                break;
        }
    }

    public void CompleteTask()
    {
        CurrentStage.CompleteTask();
        Advance();
    }
    public void FailTask(TaskResult result) => CurrentStage.FailTask(result.Reason);
    public void PassCheck(CheckResult result)
    {
        CurrentStage.PassCheck(result);
        Advance();
    }
    public void ResetCheck(CheckResult result) => CurrentStage.ResetCheck(result);
    public void PendingCheck(CheckResult result) => CurrentStage.ResetCheck(result);
    public void FailCheck(CheckResult result) => CurrentStage.FailCheck(result);
    public void ClearStageFailure() => CurrentStage.Failure = null;
    public void InjectRetryTask(string checkName, LoadedTaskInput task) => CurrentStage.InjectRetryTask(checkName, task);
    public bool HasIncompleteTaskUsing(string uses) => CurrentStage.HasIncompleteTaskUsing(uses);
    public bool HasIncompleteTaskId(string id) => CurrentStage.HasIncompleteTaskId(id);

    public void AddRuntimeTask(LoadedTaskInput task, string? stage = null, bool invalidateChecks = false)
    {
        if (!string.IsNullOrWhiteSpace(stage) && stage != CurrentStage.Stage)
            throw new WorkflowDomainException($"Cannot add runtime task to stage {stage}; current stage is {CurrentStage.Stage}");

        CurrentStage.AddRuntimeTask(task, invalidateChecks);
        _paused = false;
    }
    public int RetryCountForCheck(string checkName) => CurrentStage.RetryCountForCheck(checkName);
    public void Approve(ApprovalInput? input = null)
    {
        CurrentStage.Approve(input);
        Advance();
    }
    public void Reject(ApprovalInput? input = null) => CurrentStage.Reject(input);

    public void Retry()
    {
        if (Status != WorkflowRunStatus.Failed)
            throw new WorkflowDomainException($"WorkflowRun is {Status}, retry requires failed");
        CurrentStage.Retry();
        _paused = false;
    }

    public void Rerun()
    {
        var attempt = _stageAttempts.GetValueOrDefault(CurrentStage.Stage, 0) + 1;
        _stageAttempts[CurrentStage.Stage] = attempt;

        _stageRuns[CurrentStage.Order] = new StageRun(
            CurrentStage.Stage,
            CurrentStage.Order,
            attempt,
            CurrentStage.RequiresApproval);

        CurrentStage = _stageRuns[CurrentStage.Order];
        CurrentStage.Start();
        _paused = false;
    }

    private void Advance()
    {
        while (CurrentStage.Status == StageRunStatus.Completed)
        {
            var currentIndex = _stageRuns.IndexOf(CurrentStage);
            var nextIndex = currentIndex + 1;
            if (nextIndex >= _stageRuns.Count) return;

            CurrentStage = _stageRuns[nextIndex];
            CurrentStage.Start();
        }
    }

    private WorkflowWork MapWork(StageWork work) => work switch
    {
        StageWork.StageInit => new WorkflowWork.StageInit(CurrentStage.Stage),
        StageWork.Task t => new WorkflowWork.Task(CurrentStage.Stage, t.Id, t.Title, t.Uses, t.With),
        StageWork.Checks c => new WorkflowWork.Checks(CurrentStage.Stage, c.Items),
        _ => throw new WorkflowDomainException($"Unknown work kind: {work.GetType().Name}")
    };
}
