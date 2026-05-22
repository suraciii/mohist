using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public class StageRun
{
    private readonly List<TaskRun> _tasks = [];
    private readonly List<StageCheck> _checks = [];
    private readonly List<CheckDefinition> _staticChecks;
    private readonly Dictionary<string, int> _taskAttempts = new();
    private bool _started;
    private bool _initialized;

    public string Stage { get; }
    public int Order { get; }
    public int Attempt { get; }
    public bool RequiresApproval { get; }
    public FailureDetails? Failure { get; set; }
    public ApprovalState? Approval { get; set; }
    public IReadOnlyList<TaskRun> Tasks => _tasks.AsReadOnly();
    public IReadOnlyList<StageCheck> Checks => _checks.AsReadOnly();

    public StageRunStatus Status
    {
        get
        {
            if (Failure is not null) return StageRunStatus.Failed;
            if (!_started) return StageRunStatus.Pending;
            if (Approval?.Status == "awaiting") return StageRunStatus.AwaitingApproval;
            if (IsComplete)
            {
                if (RequiresApproval && Approval?.Status != "approved") return StageRunStatus.Running;
                return StageRunStatus.Passed;
            }
            return StageRunStatus.Running;
        }
    }

    public bool IsComplete => _initialized
        && _tasks.All(t => t.Status == TaskRunStatus.Completed)
        && _checks.All(c => c.Status == CheckRunStatus.Passed);

    public bool Initialized => _initialized;

    public TaskRun? CurrentTask =>
        _tasks.FirstOrDefault(t => t.Status != TaskRunStatus.Completed && t.Status != TaskRunStatus.Failed);

    public StageRun(
        string stage,
        int order,
        List<CheckDefinition> staticChecks,
        int attempt = 1,
        bool requiresApproval = false)
    {
        Stage = stage;
        Order = order;
        _staticChecks = staticChecks;
        Attempt = attempt;
        RequiresApproval = requiresApproval;
    }

    public void Start() => _started = true;

    public void InitTasks(List<LoadedTaskInput>? tasks = null)
    {
        if (_initialized) return;
        tasks ??= [];

        foreach (var t in tasks)
            AddTask(t);

        foreach (var c in _staticChecks)
            _checks.Add(new StageCheck(c.Name, c.Title, c.Uses, c.With));

        _initialized = true;
        Advance();
    }

    public StageWork? GetNextWork()
    {
        if (Status != StageRunStatus.Running) return null;

        if (!_initialized)
            return new StageWork.StageInit();

        var task = CurrentTask;
        if (task is not null)
            return new StageWork.Task(task.Id, task.Title, task.Uses, task.WithInput);

        var check = PendingCheck;
        if (check is not null)
            return new StageWork.Check(check.Name, check.Title, check.Uses, check.WithInput);

        return null;
    }

    public void CompleteTask()
    {
        var task = CurrentTask;
        if (task is null) return;
        task.Start();
        task.Complete();
        Advance();
    }

    public void FailTask(string? reason = null)
    {
        var task = CurrentTask;
        if (task is null) return;
        task.Start();
        task.Fail();
        Failure = new FailureDetails(FailureReason.TaskFailed, Stage, task.Id, Message: reason);
    }

    public void PassCheck(CheckResult result)
    {
        var check = RequirePendingCheck();
        check.Message = result.Message;
        check.Output = result.Output;
        check.Pass();
        Advance();
    }

    public void ResetCheck(CheckResult result)
    {
        var check = RequirePendingCheck();
        check.Message = result.Message;
        check.Output = result.Output;
        check.Reset();
    }

    public void FailCheck(CheckResult result)
    {
        var check = RequirePendingCheck();
        check.Message = result.Message;
        check.Output = result.Output;
        check.Fail();
        Failure = new FailureDetails(FailureReason.CheckUnrepaired, Stage, CheckName: check.Name, Message: result.Message);
    }

    private void RequestApproval(JsonElement? output = null)
    {
        Approval = new ApprovalState("awaiting", output, DateTime.UtcNow.ToString("O"), null);
    }

    private void Advance()
    {
        if (RequiresApproval && Approval is null && IsComplete)
            RequestApproval();
    }

    public void Approve(ApprovalInput? input = null)
    {
        if (Approval?.Status != "awaiting")
            throw new WorkflowDomainException($"Stage {Stage} is not awaiting approval");

        Approval = new ApprovalState("approved", input?.Output ?? null, Approval.RequestedAt, DateTime.UtcNow.ToString("O"));
    }

    public void Reject(ApprovalInput? input = null)
    {
        if (Approval?.Status != "awaiting")
            throw new WorkflowDomainException($"Stage {Stage} is not awaiting approval");

        var message = input?.Output?.GetString();
        Failure = new FailureDetails(FailureReason.ApprovalRejected, Stage, Message: message);
        Approval = new ApprovalState("rejected", input?.Output ?? null, Approval.RequestedAt, DateTime.UtcNow.ToString("O"));
    }

    public void InjectRetryTask(string checkName, LoadedTaskInput task)
    {
        AddTask(task);
    }

    public int RetryCountForCheck(string checkName)
    {
        return _tasks.Count(t => t.DefinitionId == checkName || t.DefinitionId.Contains($":{checkName}"));
    }

    public void Retry()
    {
        if (Failure is null)
            throw new WorkflowDomainException($"Stage {Stage} is not failed");

        var failedTask = _tasks.LastOrDefault(t => t.Status == TaskRunStatus.Failed);
        if (failedTask is not null)
        {
            AddTask(new LoadedTaskInput(failedTask.DefinitionId, failedTask.Title, failedTask.Uses, failedTask.WithInput));
            Failure = null;
            return;
        }

        foreach (var check in _checks)
            if (check.Status == CheckRunStatus.Failed)
                check.Reset();

        Failure = null;
    }

    private void AddTask(LoadedTaskInput input)
    {
        var attempt = _taskAttempts.GetValueOrDefault(input.Id, 0) + 1;
        _taskAttempts[input.Id] = attempt;
        _tasks.Add(new TaskRun(input.Id, attempt, input.Title, input.Uses, input.With));
    }

    private StageCheck? PendingCheck =>
        _checks.FirstOrDefault(c => c.Status == CheckRunStatus.Pending);

    private StageCheck RequirePendingCheck()
    {
        var check = PendingCheck;
        if (check is null)
            throw new WorkflowDomainException($"No pending check in stage {Stage}");
        return check;
    }
}
