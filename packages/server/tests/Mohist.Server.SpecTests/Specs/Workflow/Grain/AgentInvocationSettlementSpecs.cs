using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Subscriptions;
using Mohist.Workflow.Definition;
using Xunit;
using IAgentJobGrain = Mohist.Server.Agent.Grains.IAgentJobGrain;
using PrepareManualLaunchCommand = Mohist.Server.Agent.Grains.PrepareManualLaunchCommand;
using AgentJobWorkflowInvocation = Mohist.Server.Agent.Grains.AgentJobWorkflowInvocation;
using AgentJobStatus = Mohist.Server.Agent.Grains.AgentJobStatus;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// Workflow-owned completion finalization coverage (issue 559, T-004 /
/// design D7). <c>WorkflowGrain.SettleAgentInvocationAsync</c> consumes a
/// typed AgentJob terminal and applies the task completion effects
/// exactly once, guarded by the durable per-effect
/// <see cref="AgentInvocationSettlement"/> receipt: artifact binding,
/// setVars extraction/application, then the task outcome through the
/// same domain settlement calls the inline report path uses — including
/// the inline <c>expectation-failed</c> code and message for an
/// unsatisfied evaluation, and recovery <c>when</c>-matching under the
/// remaining budget for failures. Duplicate and stale terminals are
/// acknowledged without reapplying effects, and the stop cascade
/// cancels the running invocation's AgentJob.
/// </summary>
[Collection("WorkflowGrain")]
public sealed class AgentInvocationSettlementSpecs : WorkflowGrainSpecs
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);
    private const string WorkflowTerminalEventType = EventCatalog.ReverseDns.AgentJobWorkflowTerminal;

    public AgentInvocationSettlementSpecs(WorkflowGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task CompletedTerminal_CompletesTaskOnce_WithPromiseArtifactsSetVarsAndAdvancement()
    {
        var workflowId = $"wf-agent-settle-complete-{Guid.NewGuid():N}";
        var (workflow, task, link, _, _) = await StartDelegatedTaskAsync(workflowId, task => task with
        {
            SetVars = new Dictionary<string, string> { ["result"] = "output.promise" },
            Artifacts = new TaskArtifactCapture([new TaskArtifactDeclaration("plans/report.md")]),
        });
        await SeedPendingUploadAsync(workflowId, link.WorkId, task.Id, "upload-settle-1", "plans/report.md");

        var terminal = CompletedTerminal(workflowId, link, matched: "<promise>done</promise>", uploadIds: ["upload-settle-1"]);
        Assert.Equal(AgentInvocationSettlementAck.Applied, await workflow.SettleAgentInvocationAsync(terminal));

        var run = await LoadRunAsync(workflowId);
        var settled = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, settled.Status);
        Assert.Equal(
            JSON.DeserializeElement("""{"promise":"done"}""").GetRawText(),
            settled.Output!.Value.GetRawText());
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);

        // Artifacts bound and recorded exactly once.
        using (var scope = Services.CreateScope())
        {
            var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>()
                .CreateDbContextAsync();
            var artifact = Assert.Single(await db.WorkflowArtifacts
                .Where(row => row.WorkflowRunId == workflowId)
                .ToListAsync());
            Assert.Equal("upload-settle-1", artifact.SourceUploadId);
            Assert.Equal("plans/report.md", artifact.Path);
            Assert.Null(await db.WorkflowArtifactPendingUploads.FindAsync("upload-settle-1"));
        }

        // setVars applied through the same store the runner's patch route uses.
        var vars = await Services.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(workflowId);
        Assert.Equal("done", vars.Vars!.Value.GetProperty("result").GetString());

        // Advancement happened exactly once for this attempt.
        var events = await EventStore.ListAsync(workflowId);
        Assert.Single(events, e => e.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
        Assert.Single(events, e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowArtifactRecorded);
        Assert.Contains(events, e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunCompleted);

        // The receipt marks every effect applied.
        var receipt = settled.AgentInvocationSettlement;
        Assert.NotNull(receipt);
        Assert.True(receipt.IsSettled);
        Assert.True(receipt.SettlementApplied);
        Assert.True(receipt.AdvancementApplied);
        Assert.NotNull(receipt.SettledAt);
        Assert.Equal(terminal.DeliveryId, receipt.Terminal.DeliveryId);

        // A duplicate delivery (including dead-letter redelivery through the
        // handler) is acknowledged as already-applied without reapplying
        // artifacts, variables, task outcome, or advancement.
        Assert.Equal(AgentInvocationSettlementAck.AlreadyApplied, await workflow.SettleAgentInvocationAsync(terminal));
        await RedeliverThroughHandlerAsync(terminal);
        var after = await LoadRunAsync(workflowId);
        Assert.Equal(WorkflowRunStatus.Completed, after.Status);
        var eventsAfter = await EventStore.ListAsync(workflowId);
        Assert.Equal(events.Count, eventsAfter.Count);
        var varsAfter = await Services.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(workflowId);
        Assert.Equal(vars.Vars!.Value.GetRawText(), varsAfter.Vars!.Value.GetRawText());
    }

    [Fact]
    public async Task UnsatisfiedExpectation_FailsTaskWithInlineExpectationFailedCodeAndMessage()
    {
        var workflowId = $"wf-agent-settle-expect-{Guid.NewGuid():N}";
        var (workflow, task, link, _, _) = await StartDelegatedTaskAsync(workflowId);
        const string message =
            "Workflow completion requirements were not satisfied: missing marker in _output: <promise>done</promise>";

        var terminal = CompletedTerminal(workflowId, link, matched: "<promise>done</promise>", satisfied: false, message: message);
        Assert.Equal(AgentInvocationSettlementAck.Applied, await workflow.SettleAgentInvocationAsync(terminal));

        var run = await LoadRunAsync(workflowId);
        var settled = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Failed, settled.Status);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        // The matched promise projects into output.promise exactly as the
        // inline executor does, even on the failure path.
        Assert.Equal(
            JSON.DeserializeElement("""{"promise":"done"}""").GetRawText(),
            settled.Output!.Value.GetRawText());
        Assert.Equal("expectation-failed", settled.Error!.Code);
        Assert.Equal(message, settled.Error.Message);
        Assert.Equal(message, run.Failure!.Message);

        var events = await EventStore.ListAsync(workflowId);
        Assert.Single(events, e => e.Envelope.Type == EventCatalog.ReverseDns.TaskFailed);
        Assert.Single(events, e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunFailed);
    }

    [Fact]
    public async Task SetVarsFailure_FailsTaskWithTheExistingSetVarsMessage()
    {
        var workflowId = $"wf-agent-settle-setvars-failure-{Guid.NewGuid():N}";
        var recovery = new RecoveryDefinition(
            1,
            [new RecoveryHandlerDefinition(
                "error.code=set-vars-failure",
                [new TaskDefinition("recover:fix", "Fix", "spec/fix")],
                RetrySelf: true)]);
        var (workflow, _, link, _, _) = await StartDelegatedTaskAsync(
            workflowId,
            customize: t => t with
            {
                SetVars = new Dictionary<string, string> { ["result"] = "output.missing" },
                Recovery = recovery,
            });

        Assert.Equal(
            AgentInvocationSettlementAck.Applied,
            await workflow.SettleAgentInvocationAsync(
                CompletedTerminal(workflowId, link, matched: "<promise>done</promise>")));

        var run = await LoadRunAsync(workflowId);
        var task = Assert.Single(run.CurrentStage().Tasks, candidate => candidate.Id == "agent-task.1");
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal(
            "setVars: setVars source path 'output.missing' not found in task output",
            run.Failure!.Message);
        Assert.Null(task.Error);
        Assert.DoesNotContain(run.CurrentStage().Tasks, candidate => candidate.DefinitionId == "recover:fix");
    }

    [Fact]
    public async Task TerminalWithDifferentInvocationLineage_IsAcknowledgedAsStale()
    {
        var workflowId = $"wf-agent-settle-lineage-{Guid.NewGuid():N}";
        var (workflow, task, link, _, _) = await StartDelegatedTaskAsync(workflowId);

        var stale = CompletedTerminal(workflowId, link, matched: "<promise>done</promise>") with
        {
            JobId = "agent-job-from-another-invocation",
            DeliveryId = "workflow-terminal:agent-job-from-another-invocation",
        };

        Assert.Equal(AgentInvocationSettlementAck.Stale, await workflow.SettleAgentInvocationAsync(stale));
        var run = await LoadRunAsync(workflowId);
        Assert.Equal(TaskRunStatus.Running, Assert.Single(run.CurrentStage().Tasks).Status);
        Assert.Null(task.AgentInvocationSettlement);
    }

    [Fact]
    public async Task FailedTerminal_WithMatchedRecoveryHandler_SchedulesContinuationsUnderBudget()
    {
        var workflowId = $"wf-agent-settle-recover-{Guid.NewGuid():N}";
        var recovery = new RecoveryDefinition(
            1,
            [
                new RecoveryHandlerDefinition(
                    "error.code=agent-exploded",
                    [new TaskDefinition("recover:fix", "Fix", "spec/fix")],
                    RetrySelf: true),
            ]);
        var (workflow, task, link, _, _) = await StartDelegatedTaskAsync(workflowId, t => t with { Recovery = recovery });

        var terminal = FailedTerminal(workflowId, link, failureReason: "agent-exploded", message: "the runtime exploded");
        Assert.Equal(AgentInvocationSettlementAck.Applied, await workflow.SettleAgentInvocationAsync(terminal));

        var run = await LoadRunAsync(workflowId);
        var attempted = Assert.Single(run.CurrentStage().Tasks, t => t.Id == task.Id);
        // The failed attempt completed with the recovery-scheduled outcome,
        // exactly like an inline report carrying the runner's addTasks.
        Assert.Equal(TaskRunStatus.Completed, attempted.Status);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.NotNull(run.CurrentStage().Tasks.SingleOrDefault(t => t.Id == "recover:fix.1"));
        var retry = run.CurrentStage().Tasks.SingleOrDefault(t => t.DefinitionId == task.DefinitionId && t.Attempt == 2);
        Assert.NotNull(retry);
        Assert.Equal(0, retry.RecoveryRemaining);
    }

    [Fact]
    public async Task FailedTerminal_WithoutMatchingHandlerOrBudget_FailsTheTask()
    {
        var workflowId = $"wf-agent-settle-fail-{Guid.NewGuid():N}";
        var recovery = new RecoveryDefinition(
            1,
            [new RecoveryHandlerDefinition("error.code=other", [new TaskDefinition("recover:fix", "Fix", "spec/fix")], RetrySelf: false)]);
        var (workflow, _, link, _, _) = await StartDelegatedTaskAsync(workflowId, t => t with { Recovery = recovery });

        var terminal = FailedTerminal(workflowId, link, failureReason: "agent-exploded", message: "the runtime exploded");
        Assert.Equal(AgentInvocationSettlementAck.Applied, await workflow.SettleAgentInvocationAsync(terminal));

        var run = await LoadRunAsync(workflowId);
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(run.CurrentStage().Tasks).Status);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("the runtime exploded", run.Failure!.Message);
        Assert.Equal("agent-exploded", run.Failure.Error!.Code);
        Assert.Null(run.CurrentStage().Tasks.SingleOrDefault(t => t.Id == "recover:fix.1"));

        // A later retry of the run follows the normal retry semantics. This
        // run is already assigned to its worker, so RetryAsync returns it to
        // the dispatchable Ready state.
        await workflow.RetryAsync();
        var retried = await LoadRunAsync(workflowId);
        Assert.Equal(WorkflowRunStatus.Ready, retried.Status);
    }

    [Fact]
    public async Task StaleTerminal_IsAcknowledgedWithoutApplyingEffects()
    {
        // (a) Task already terminal: the run was stopped while the terminal
        // was in flight — the stop settles the task under stop semantics.
        var stoppedId = $"wf-agent-settle-stale-stop-{Guid.NewGuid():N}";
        var (stoppedWorkflow, stoppedLink, stoppedTaskId) = await StartDelegatedTaskForStopAsync(stoppedId);
        await stoppedWorkflow.StopAsync("operator stop");
        var stoppedRun = await LoadRunAsync(stoppedId);
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(stoppedRun.CurrentStage().Tasks).Status);

        Assert.Equal(
            AgentInvocationSettlementAck.Stale,
            await stoppedWorkflow.SettleAgentInvocationAsync(CompletedTerminal(stoppedId, stoppedLink, matched: "<promise>done</promise>")));
        var afterStop = await LoadRunAsync(stoppedId);
        Assert.Equal(WorkflowRunStatus.Stopped, afterStop.Status);
        Assert.Null(Assert.Single(afterStop.CurrentStage().Tasks).AgentInvocationSettlement);
        Assert.DoesNotContain(await EventStore.ListAsync(stoppedId),
            e => e.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
        Assert.True(stoppedTaskId.Length > 0);

        // (b) Unknown attempt: acknowledged without effects.
        var unknownId = $"wf-agent-settle-stale-unknown-{Guid.NewGuid():N}";
        var (unknownWorkflow, unknownLink, _) = await StartDelegatedTaskForStopAsync(unknownId);
        Assert.Equal(
            AgentInvocationSettlementAck.Stale,
            await unknownWorkflow.SettleAgentInvocationAsync(
                CompletedTerminal(unknownId, unknownLink with { TaskRunId = "missing-task-run.9" }, matched: null)));
        var unknownRun = await LoadRunAsync(unknownId);
        Assert.Equal(TaskRunStatus.Running, Assert.Single(unknownRun.CurrentStage().Tasks).Status);
    }

    [Fact]
    public async Task StopAsync_CascadesCancellationToTheInvocationAgentJob_AndCancelledTerminalSettlesUnderStopSemantics()
    {
        var workflowId = $"wf-agent-settle-stop-{Guid.NewGuid():N}";
        var (workflow, task, link, _, _) = await StartDelegatedTaskAsync(workflowId);

        // The reserved AgentJob exists as a pending workflow-originated job
        // (the exact participant shape the handoff activation produces).
        var job = Grains.GetGrain<IAgentJobGrain>(link.JobId);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: link.SessionId,
            InputId: link.InputId,
            TurnId: link.TurnId,
            Prompt: "run the workflow agent task",
            ProjectId: TestProjectId(workflowId),
            AgentId: "agent-stop-cascade",
            WorkflowRunId: workflowId,
            Skills: [],
            WorkflowInvocation: new AgentJobWorkflowInvocation(
                link.InvocationId,
                link.TaskRunId,
                link.WorkId)));
        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());

        await workflow.StopAsync("operator stop");

        // The cascade cancelled the AgentJob; the task settles under the
        // existing stop semantics.
        Assert.Equal(AgentJobStatus.Cancelled, await job.GetStatusAsync());
        var run = await LoadRunAsync(workflowId);
        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(run.CurrentStage().Tasks).Status);

        // The cancelled terminal arrives later and settles as a stale
        // delivery under the existing stop semantics.
        Assert.Equal(
            AgentInvocationSettlementAck.Stale,
            await workflow.SettleAgentInvocationAsync(CancelledTerminal(workflowId, link)));
        var after = await LoadRunAsync(workflowId);
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(after.CurrentStage().Tasks).Status);
    }

    /// <summary>
    /// Convergence proof: an inline task and a handoff task given the same
    /// terminal facts reach the same domain outcome — both settlement paths
    /// converge on the same domain calls (ApplyTaskReportAsync settlement,
    /// artifact bind service, variable store).
    /// </summary>
    [Fact]
    public async Task InlineAndHandoffTasks_ConvergeOnTheSameTaskOutcome()
    {
        // Inline side: a runtime agent task whose runner-reported result is
        // the projected agent-turn outcome for the same terminal facts
        // (satisfied evaluation, matched promise, bound upload).
        var inlineId = $"wf-agent-settle-inline-{Guid.NewGuid():N}";
        var (inlineWorkflow, inlineTask, _, inlineWork, inlineRunner) = await StartDelegatedTaskAsync(
            inlineId,
            customize: null,
            uses: "mohist/opencode",
            setVars: new Dictionary<string, string> { ["result"] = "output.promise" },
            bindLink: false);
        await SeedPendingUploadAsync(inlineId, inlineWork.WorkId, inlineTask.Id, "upload-inline-1", "plans/report.md");
        await ReportAsync(inlineRunner, inlineWork.WorkId, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("""{"promise":"done"}"""),
            ArtifactUploadIds: ["upload-inline-1"]));

        // Handoff side: the same terminal facts settle through the finalizer.
        var handoffId = $"wf-agent-settle-handoff-{Guid.NewGuid():N}";
        var (handoffWorkflow, handoffTask, link, _, _) = await StartDelegatedTaskAsync(
            handoffId,
            customize: t => t with { SetVars = new Dictionary<string, string> { ["result"] = "output.promise" } });
        await SeedPendingUploadAsync(handoffId, link.WorkId, handoffTask.Id, "upload-handoff-1", "plans/report.md");
        await handoffWorkflow.SettleAgentInvocationAsync(CompletedTerminal(
            handoffId,
            link,
            matched: "<promise>done</promise>",
            uploadIds: ["upload-handoff-1"]));

        var inlineRun = await LoadRunAsync(inlineId);
        var handoffRun = await LoadRunAsync(handoffId);
        var inlineSettled = Assert.Single(inlineRun.CurrentStage().Tasks);
        var handoffSettled = Assert.Single(handoffRun.CurrentStage().Tasks);
        Assert.Equal(inlineSettled.Status, handoffSettled.Status);
        Assert.Equal(inlineSettled.Output!.Value.GetRawText(), handoffSettled.Output!.Value.GetRawText());
        Assert.Equal(inlineRun.Status, handoffRun.Status);

        // The inline report fixture above bypasses the Runner's setVars
        // side effect; the handoff path owns that effect in its finalizer.
        // The dedicated completion test verifies the persisted variable value.
        var handoffVars = await Services.GetRequiredService<WorkflowRunVariablesStore>().GetVariablesAsync(handoffId);
        Assert.Equal("done", handoffVars.Vars!.Value.GetProperty("result").GetString());

        var inlineEvents = (await EventStore.ListAsync(inlineId)).Select(e => e.Envelope.Type).ToList();
        var handoffEvents = (await EventStore.ListAsync(handoffId)).Select(e => e.Envelope.Type).ToList();
        Assert.Equal(inlineEvents, handoffEvents);
    }

    [Fact]
    public async Task WorkflowTerminalEvent_ThroughTheBus_SettlesTheTask()
    {
        var workflowId = $"wf-agent-settle-bus-{Guid.NewGuid():N}";
        var (_, _, link, _, _) = await StartDelegatedTaskAsync(workflowId);

        // The durable dispatcher delivers the appended workflow-terminal
        // event to the Workflow-owned subscription handler, which invokes
        // the finalizer on the owning grain.
        await EventStore.AppendAsync(BuildTerminalEnvelope(
            CompletedTerminal(workflowId, link, matched: "<promise>done</promise>")));
        using (var scope = Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventDispatcherService>()
                .DispatchAsync(CancellationToken.None);
        }

        await TestWait.ForAsync(
            async () => await LoadRunAsync(workflowId),
            run => Assert.Single(run.CurrentStage().Tasks).Status == TaskRunStatus.Completed,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(25),
            "handoff task to settle through the workflow-terminal event");
    }

    [Fact]
    public async Task BindingMohistAgentInvocation_ClearsInlineResultSettlement()
    {
        var workflowId = $"wf-agent-settle-binding-{Guid.NewGuid():N}";
        var workflow = await StartWorkflowAsync(
            SingleStage(
                tasks:
                [
                    new TaskDefinition(
                        "agent-task",
                        "Agent task",
                        "mohist/agent",
                        With: new Dictionary<string, JsonElement?>
                        {
                            ["prompt"] = JSON.SerializeToElement("run the task"),
                        },
                        Expect: ExpectForPromiseMarker()),
                ],
                checks: []),
            id: workflowId);
        await workflow.AssignWorkerAsync(_runnerId!);

        var work = await workflow.ClaimNextAsync(_runnerId!);
        Assert.NotNull(work);
        var run = await LoadRunAsync(workflowId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.NotNull(task.AgentResultSettlement);

        var link = LinkFor(task, work!.Id!);
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentInvocationAsync(link));

        run = await LoadRunAsync(workflowId);
        task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Null(task.AgentResultSettlement);
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentInvocationAsync(link));
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private async Task<(IWorkflowGrain Workflow, TaskRun Task, AgentInvocationLink Link, WorkDispatch Work, string RunnerId)>
        StartDelegatedTaskAsync(
            string workflowId,
            Func<TaskDefinition, TaskDefinition>? customize = null,
            string uses = "spec/task",
            Dictionary<string, string>? setVars = null,
            bool bindLink = true)
    {
        var definition = new TaskDefinition(
            "agent-task",
            "Agent task",
            uses,
            With: new Dictionary<string, JsonElement?> { ["prompt"] = JSON.SerializeToElement("run the task") },
            Expect: ExpectForPromiseMarker(),
            SetVars: setVars);
        if (customize is not null)
            definition = customize(definition);

        var workflow = await StartWorkflowAsync(SingleStage(tasks: [definition], checks: []), id: workflowId);
        var (work, runnerId) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(workflowId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(work.WorkId, task.WorkId);

        if (bindLink)
        {
            var link = LinkFor(task, work.WorkId);
            Assert.Equal(ReportAck.Accepted, await workflow.BindAgentInvocationAsync(link));
            // The linkage write is idempotent and a conflicting linkage is stale.
            Assert.Equal(ReportAck.Accepted, await workflow.BindAgentInvocationAsync(link));
            Assert.Equal(ReportAck.Stale, await workflow.BindAgentInvocationAsync(link with { JobId = "other-job" }));
            return (workflow, task, link, work, runnerId);
        }

        return (workflow, task, LinkFor(task, work.WorkId), work, runnerId);
    }

    private async Task<(IWorkflowGrain Workflow, AgentInvocationLink Link, string TaskRunId)> StartDelegatedTaskForStopAsync(
        string workflowId)
    {
        var (workflow, task, link, _, _) = await StartDelegatedTaskAsync(workflowId);
        return (workflow, link, task.Id);
    }

    private static Dictionary<string, JsonElement?> ExpectForPromiseMarker() => new(StringComparer.Ordinal)
    {
        ["markers"] = JSON.DeserializeElement(
            """[{"path":"_output","oneOf":["<promise>done</promise>","<promise>unfinished</promise>"]}]"""),
    };

    private static AgentInvocationLink LinkFor(TaskRun task, string workId) => new(
        InvocationId: $"workflow-agent-invocation-{workId}",
        TaskRunId: task.Id,
        WorkId: workId,
        JobId: $"agent-job-{workId}",
        SessionId: $"agent-session-{workId}",
        InputId: $"workflow-agent-input-{workId}",
        TurnId: $"workflow-agent-turn-{workId}");

    private static AgentInvocationTerminal CompletedTerminal(
        string workflowRunId,
        AgentInvocationLink link,
        string? matched,
        bool satisfied = true,
        string? message = null,
        string[]? uploadIds = null) => new(
        DeliveryId: $"workflow-terminal:{link.JobId}",
        InvocationId: link.InvocationId,
        ProjectId: "proj-settle",
        WorkflowRunId: workflowRunId,
        TaskRunId: link.TaskRunId,
        WorkId: link.WorkId,
        JobId: link.JobId,
        SessionId: link.SessionId,
        InputId: link.InputId,
        TurnId: link.TurnId,
        Status: AgentInvocationTerminalStatus.Completed,
        Message: "AgentJob completed",
        FailureReason: null,
        FailureCategory: null,
        ExitCode: 0,
        ArtifactUploadIds: uploadIds,
        Expectation: new AgentInvocationExpectation(
            satisfied,
            matched,
            message ?? (satisfied
                ? "Workflow completion requirements satisfied"
                : "Workflow completion requirements were not satisfied")),
        RecordedAt: RecordedAt);

    private static AgentInvocationTerminal FailedTerminal(
        string workflowRunId,
        AgentInvocationLink link,
        string failureReason,
        string message) => new(
        DeliveryId: $"workflow-terminal:{link.JobId}",
        InvocationId: link.InvocationId,
        ProjectId: "proj-settle",
        WorkflowRunId: workflowRunId,
        TaskRunId: link.TaskRunId,
        WorkId: link.WorkId,
        JobId: link.JobId,
        SessionId: link.SessionId,
        InputId: link.InputId,
        TurnId: link.TurnId,
        Status: AgentInvocationTerminalStatus.Failed,
        Message: message,
        FailureReason: failureReason,
        FailureCategory: failureReason,
        ExitCode: 1,
        ArtifactUploadIds: null,
        Expectation: null,
        RecordedAt: RecordedAt);

    private static AgentInvocationTerminal CancelledTerminal(string workflowRunId, AgentInvocationLink link) => new(
        DeliveryId: $"workflow-terminal:{link.JobId}",
        InvocationId: link.InvocationId,
        ProjectId: "proj-settle",
        WorkflowRunId: workflowRunId,
        TaskRunId: link.TaskRunId,
        WorkId: link.WorkId,
        JobId: link.JobId,
        SessionId: link.SessionId,
        InputId: link.InputId,
        TurnId: link.TurnId,
        Status: AgentInvocationTerminalStatus.Cancelled,
        Message: "cancelled",
        FailureReason: "cancelled",
        FailureCategory: null,
        ExitCode: null,
        ArtifactUploadIds: null,
        Expectation: null,
        RecordedAt: RecordedAt);

    /// <summary>
    /// Re-delivers the terminal through the subscription handler with the
    /// same event identity — the dead-letter redelivery shape.
    /// </summary>
    private async Task RedeliverThroughHandlerAsync(AgentInvocationTerminal terminal)
    {
        var handler = Services.GetRequiredService<AgentJobWorkflowTerminalHandler>();
        var envelope = BuildTerminalEnvelope(terminal);
        await handler.HandleAsync(envelope, CancellationToken.None);
        await handler.HandleAsync(envelope, CancellationToken.None);
    }

    internal static CloudEvent BuildTerminalEnvelope(AgentInvocationTerminal terminal)
    {
        var data = JSON.SerializeToElement(new
        {
            invocationId = terminal.InvocationId,
            projectId = terminal.ProjectId,
            workflowRunId = terminal.WorkflowRunId,
            taskRunId = terminal.TaskRunId,
            workId = terminal.WorkId,
            jobId = terminal.JobId,
            sessionId = terminal.SessionId,
            inputId = terminal.InputId,
            turnId = terminal.TurnId,
            status = terminal.Status.ToString().ToLowerInvariant(),
            message = terminal.Message,
            failureReason = terminal.FailureReason,
            failureCategory = terminal.FailureCategory,
            exitCode = terminal.ExitCode,
            artifactUploadIds = terminal.ArtifactUploadIds,
            evaluation = terminal.Expectation is null
                ? null
                : new
                {
                    satisfied = terminal.Expectation.Satisfied,
                    matched = terminal.Expectation.Matched,
                    missingFiles = Array.Empty<object>(),
                    missingMarkers = Array.Empty<object>(),
                    failIfMatches = Array.Empty<object>(),
                    message = terminal.Expectation.Message,
                },
            recordedAt = terminal.RecordedAt,
        });
        return new CloudEvent(
            id: terminal.DeliveryId,
            source: new Uri($"/mohist/agent-job/{terminal.JobId}", UriKind.Relative),
            type: WorkflowTerminalEventType,
            time: terminal.RecordedAt,
            data: data,
            subject: terminal.JobId);
    }

    private async Task SeedPendingUploadAsync(
        string workflowRunId,
        string workId,
        string taskRunId,
        string uploadId,
        string path)
    {
        using var scope = Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            TaskRunId = taskRunId,
            Path = path,
            StoragePath = $"/mohist-tests/artifacts/{uploadId}",
            CreatedAt = RecordedAt,
            ExpiresAt = RecordedAt + TimeSpan.FromHours(1),
        });
        await db.SaveChangesAsync();
    }
}
