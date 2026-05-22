using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public class WorkflowRun
{
    private readonly List<StageDefinition> _definitionStages;
    private readonly List<StageRun> _stageRuns;
    private bool _started;
    private bool _paused;

    public string Id { get; }
    public IReadOnlyList<StageRun> StageRuns => _stageRuns.AsReadOnly();
    public StageRun CurrentStage { get; private set; }
    public bool PauseRequested { get; set; }

    public WorkflowRunStatus Status
    {
        get
        {
            if (!_started) return WorkflowRunStatus.Pending;
            if (_paused) return WorkflowRunStatus.Paused;
            if (CurrentStage.Status == StageRunStatus.Failed) return WorkflowRunStatus.Failed;
            if (CurrentStage.Status == StageRunStatus.Passed
                && CurrentStage == _stageRuns[^1]) return WorkflowRunStatus.Passed;
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

        _stageRuns = definitionStages.Select((def, index) =>
            new StageRun(
                def.Stage,
                index,
                def.Tasks,
                def.Checks,
                def.TasksFrom,
                def.RequiresApproval
            )).ToList();

        CurrentStage = _stageRuns[0];
    }

    public void Start()
    {
        if (Status != WorkflowRunStatus.Pending && Status != WorkflowRunStatus.Paused)
            throw new WorkflowDomainException($"WorkflowRun is {Status}");

        _started = true;
        _paused = false;
        PauseRequested = false;

        if (CurrentStage.Status == StageRunStatus.Pending)
            CurrentStage.Start();
    }

    public WorkflowWork Next()
    {
        var status = Status;
        if (status == WorkflowRunStatus.Passed)
            return new WorkflowWork.Complete(CurrentStage.Stage);

        if (status == WorkflowRunStatus.Failed)
        {
            if (Failure is null)
                throw new WorkflowDomainException("Failed WorkflowRun requires failure details");
            return new WorkflowWork.Failed(Failure);
        }

        if (status != WorkflowRunStatus.Running)
            return new WorkflowWork.Blocked(CurrentStage.Stage, "workflow-not-running");

        var work = CurrentStage.NextWork();

        if (work is StageWork.AwaitApproval && CurrentStage.Status == StageRunStatus.Running)
            CurrentStage.RequestApproval();

        if (work is StageWork.Complete)
        {
            if (PassStage())
                return new WorkflowWork.Complete(CurrentStage.Stage);
            return Next();
        }

        if (work is StageWork.Blocked)
        {
            if (Failure is null)
                throw new WorkflowDomainException("Failed stage requires failure details");
            return new WorkflowWork.Failed(Failure);
        }

        return MapWork(work);
    }

    public void RequestPause()
    {
        if (Status == WorkflowRunStatus.Running)
            PauseRequested = true;
    }

    public void Pause()
    {
        if (Status != WorkflowRunStatus.Running) return;
        _paused = true;
        PauseRequested = false;
    }

    public void InitTasks(List<LoadedTaskInput>? tasks = null) =>
        CurrentStage.InitTasks(tasks);

    public void FailStage(string reason) =>
        CurrentStage.Failure = new FailureDetails(FailureReason.TaskFailed, CurrentStage.Stage, Message: reason);

    public void CompleteTask() => CurrentStage.CompleteTask();
    public void FailTask(TaskResult result) => CurrentStage.FailTask(result.Reason);
    public void PassCheck(CheckResult result) => CurrentStage.PassCheck(result);
    public void ResetCheck(CheckResult result) => CurrentStage.ResetCheck(result);
    public void PendingCheck(CheckResult result) => CurrentStage.ResetCheck(result);
    public void FailCheck(CheckResult result) => CurrentStage.FailCheck(result);
    public void ClearStageFailure() => CurrentStage.Failure = null;
    public void InjectRetryTask(string checkName, LoadedTaskInput task) => CurrentStage.InjectRetryTask(checkName, task);
    public int RetryCountForCheck(string checkName) => CurrentStage.RetryCountForCheck(checkName);
    public void Approve(ApprovalInput? input = null) => CurrentStage.Approve(input);
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
        CurrentStage.Reset();
        _paused = false;
    }

    private bool PassStage()
    {
        if (!CurrentStage.IsComplete) return false;
        if (CurrentStage.RequiresApproval && CurrentStage.Approval?.Status != "approved") return false;

        var nextIndex = CurrentStage.Order + 1;
        if (nextIndex >= _stageRuns.Count) return true;

        CurrentStage = _stageRuns[nextIndex];
        CurrentStage.Start();
        return false;
    }

    private WorkflowWork MapWork(StageWork work) => work switch
    {
        StageWork.StageInit si => new WorkflowWork.StageInit(CurrentStage.Stage, si.TasksFrom),
        StageWork.Task t => new WorkflowWork.Task(CurrentStage.Stage, t.Id, t.Title, t.Uses, t.With),
        StageWork.Check c => new WorkflowWork.Check(CurrentStage.Stage, c.Name, c.Title, c.Uses, c.With),
        StageWork.AwaitApproval => new WorkflowWork.AwaitApproval(CurrentStage.Stage),
        StageWork.Complete => new WorkflowWork.Complete(CurrentStage.Stage),
        StageWork.Blocked b => new WorkflowWork.Blocked(CurrentStage.Stage, b.Reason),
        _ => throw new WorkflowDomainException($"Unknown work kind: {work.GetType().Name}")
    };
}
