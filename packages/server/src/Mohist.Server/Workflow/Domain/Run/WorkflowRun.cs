using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public class WorkflowRun
{
    private readonly List<StageDefinition> _definitionStages;
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
            if (CurrentStage.Status == StageRunStatus.Passed && IsLastDefinitionStage(CurrentStage)) return WorkflowRunStatus.Passed;
            return WorkflowRunStatus.Running;
        }
    }

    public FailureDetails? Failure => CurrentStage.Failure;

    public IReadOnlyList<string> StageOrder =>
        _definitionStages.Select(d => d.Stage).ToList().AsReadOnly();

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

    public WorkflowRun(string id, List<StageDefinition> definitionStages)
    {
        if (definitionStages.Count == 0)
            throw new WorkflowDomainException("WorkflowRun requires at least one stage definition");

        Id = id;
        _definitionStages = definitionStages;

        var first = AppendStageRun(0);
        CurrentStage = first;
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

    public void InitTasks(List<LoadedTaskInput> tasks)
    {
        CurrentStage.InitTasks(tasks);
        Advance();
    }

    public void FailStage(string reason) =>
        CurrentStage.Failure = new FailureDetails(FailureReason.TaskFailed, CurrentStage.Stage, Message: reason);

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
        var defIndex = _definitionStages.FindIndex(d => d.Stage == CurrentStage.Stage);
        var newRun = AppendStageRun(defIndex);
        CurrentStage = newRun;
        CurrentStage.Start();
        _paused = false;
    }

    private void Advance()
    {
        while (CurrentStage.Status == StageRunStatus.Passed)
        {
            var defIndex = _definitionStages.FindIndex(d => d.Stage == CurrentStage.Stage);
            var nextDefIndex = defIndex + 1;
            if (nextDefIndex >= _definitionStages.Count) return;

            var next = AppendStageRun(nextDefIndex);
            CurrentStage = next;
            CurrentStage.Start();
        }
    }

    private StageRun AppendStageRun(int definitionIndex)
    {
        var def = _definitionStages[definitionIndex];
        var attempt = _stageAttempts.GetValueOrDefault(def.Stage, 0) + 1;
        _stageAttempts[def.Stage] = attempt;

        var run = new StageRun(
            def.Stage,
            definitionIndex,
            def.Checks,
            attempt,
            def.RequiresApproval);

        _stageRuns.Add(run);
        return run;
    }

    private bool IsLastDefinitionStage(StageRun stageRun)
    {
        var lastDef = _definitionStages[^1];
        return stageRun.Stage == lastDef.Stage && stageRun.Order == _definitionStages.Count - 1;
    }

    private WorkflowWork MapWork(StageWork work) => work switch
    {
        StageWork.StageInit => new WorkflowWork.StageInit(CurrentStage.Stage),
        StageWork.Task t => new WorkflowWork.Task(CurrentStage.Stage, t.Id, t.Title, t.Uses, t.With),
        StageWork.Checks c => new WorkflowWork.Checks(CurrentStage.Stage, c.Items),
        _ => throw new WorkflowDomainException($"Unknown work kind: {work.GetType().Name}")
    };
}
