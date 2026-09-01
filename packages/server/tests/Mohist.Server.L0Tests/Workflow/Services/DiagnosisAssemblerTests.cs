using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Services;

public sealed class DiagnosisAssemblerTests
{
    [Fact]
    public void FailedTaskIsFirstAndUsesFrozenRenderedInput()
    {
        var failed = Task("failed.1", "failed", 1, "runner/run", error: new ExecutionError("boom", "failed"));
        var remaining = Task("remaining.1", "remaining", 1, "core/check");
        var run = Run(
            new StageRun
            {
                Id = "build",
                Attempt = 1,
                RequiresApproval = false,
                Status = StageRunStatus.Failed,
                Tasks = [remaining, failed]
            },
            new FailureDetails(FailureReason.TaskFailed, "build", failed.Id, Message: "failed"));

        var view = DiagnosisAssembler.Assemble(
            run,
            """{"workflowRunId":"run-1","workId":"failed-work","with":"{\"input\":\"frozen\"}","workspace":"/proc/1/fd/4"}""",
            []);

        Assert.Equal("failed.1", view.Tasks[0].TaskId);
        Assert.Equal("remaining.1", view.Tasks[1].TaskId);
        Assert.Equal("frozen", view.Tasks[0].RenderedWith?.GetProperty("input").GetString());
        Assert.Equal("named", view.Tasks[0].Workspace.Binding);
        Assert.Equal("/workspaces/project", view.Tasks[0].Workspace.Path);
        Assert.DoesNotContain("/proc/1/fd/4", view.Dispatch.Snapshot?.GetRawText());
    }

    [Fact]
    public void MissingTaskAndSnapshotPreserveFailureWithoutFabrication()
    {
        var run = Run(
            new StageRun
            {
                Id = "checks",
                Attempt = 1,
                RequiresApproval = false,
                Status = StageRunStatus.Failed,
                Tasks = [Task("known.1", "known", 1, "runner/run")]
            },
            new FailureDetails(FailureReason.CheckFailed, "checks", CheckName: "verify", Message: "check failed"));

        run.Workspace = null;
        var view = DiagnosisAssembler.Assemble(run, null, []);

        Assert.Equal("verify", view.Failure?.CheckName);
        Assert.Single(view.Tasks);
        Assert.Equal("missing", view.Dispatch.Status);
        Assert.Equal("fallback", view.Tasks[0].Workspace.Binding);
        Assert.Null(view.Tasks[0].Workspace.Path);
    }

    [Fact]
    public void EventTailIsBoundedAndProcessScopedValuesAreExcluded()
    {
        var events = Enumerable.Range(1, 3)
            .Select(id => new StoredCloudEvent(
                id,
                new CloudEvent(
                    $"event-{id}",
                    new Uri("urn:mohist:test"),
                    "mohist.workflow.task.failed",
                    DateTimeOffset.UnixEpoch.AddSeconds(id),
                    JsonSerializer.SerializeToElement(new { path = id == 3 ? "/proc/8/fd/9" : $"logical-{id}" }))))
            .ToList();

        var view = DiagnosisAssembler.Assemble(Run(null, null), null, events, eventLimit: 2);

        Assert.Equal([2L, 3L], view.Events.Select(e => e.Id));
        Assert.DoesNotContain("/proc/8/fd/9", view.Events[1].Data?.GetRawText());
    }

    private static WorkflowRun Run(StageRun? stage, FailureDetails? failure) => new()
    {
        Id = "run-1",
        Metadata = new WorkflowRunMetadata(null, DateTimeOffset.UnixEpoch),
        Status = failure is null ? WorkflowRunStatus.Running : WorkflowRunStatus.Failed,
        CurrentStageId = stage?.Id,
        Stages = stage is null ? [] : [stage],
        Failure = failure,
        Workspace = new WorkspaceIdentity("/workspaces/project", "main")
    };

    private static WorkflowActionAttempt Task(
        string id,
        string title,
        int attempt,
        string uses,
        ExecutionError? error = null) => new()
        {
            Id = id,
            DefinitionId = title,
            Attempt = attempt,
            Title = title,
            Uses = uses,
            WithInput = new Dictionary<string, JsonElement?>
            {
                ["input"] = JsonSerializer.SerializeToElement("fallback")
            },
            Status = error is null ? WorkflowActionAttemptStatus.Pending : WorkflowActionAttemptStatus.Failed,
            Error = error,
            Recovery = new RecoveryDefinition(
                3,
                [new RecoveryHandlerDefinition("failed", [new TaskDefinition("repair")], RetrySelf: false)]),
            RecoveryRemaining = 2
        };
}
