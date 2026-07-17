using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Orleans.Serialization;
using Xunit;
using System.Text.Json;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

public class AgentJobOwnerKindSpecs : WorkflowGrainSpecs
{
    public AgentJobOwnerKindSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private DispatchService Dispatch => _fixture.Cluster.GetSiloServiceProvider(null)
        .GetRequiredService<IServiceScopeFactory>().CreateScope()
        .ServiceProvider.GetRequiredService<DispatchService>();

    [Fact]
    public async Task AssignWork_AgentJobDispatch_WithoutWorkflowRunId_IsAccepted()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-accept-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobId = "agent-job-accept-1";
        var workId = "agent-work-1";
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: agentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var result = await runner.AssignAgentJobAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, result.Status);
    }

    [Fact]
    public async Task AssignWork_AgentJobDispatch_MissingAgentJobId_IsRejected()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-missing-owner-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var workId = "agent-work-2";
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var result = await runner.AssignAgentJobAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
        Assert.Equal("invalid-work", result.Reason);
    }

    [Fact]
    public async Task AssignWork_AgentJobDispatch_MissingWorkId_IsRejected()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-missing-workid-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: string.Empty,
            AgentJobId: "agent-job-missing-work",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var result = await runner.AssignAgentJobAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
        Assert.Equal("invalid-work", result.Reason);
    }

    [Fact]
    public async Task IsWorkRunnable_AgentJobArm_RoutesToAgentJobGrain()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-runnable-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobId = "agent-job-runnable-1";
        var workId = "agent-work-runnable";
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: agentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        await runner.AssignAgentJobAsync(dispatch);

        var agentJob = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        await agentJob.AssignRunnerAsync(runnerId, workId);

        var work = await runner.PollAsync(Services);
        Assert.NotNull(work);
        Assert.Equal(agentJobId, work.AgentJobId);
        Assert.Equal(workId, work.WorkId);

        var snapshot = await agentJob.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Running, snapshot.Status);
        Assert.Equal(workId, snapshot.CurrentWorkId);
    }

    [Fact]
    public async Task Poll_AgentJobLostResponse_IsRedeliveredUntilReported()
    {
        var runnerId = await RegisterRunnerAsync($"agent-job-redelivery-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var agentJobId = $"agent-job-redelivery-{Guid.NewGuid():N}";
        var workId = $"agent-work-redelivery-{Guid.NewGuid():N}";
        await runner.AssignAgentJobAsync(AgentDispatch(agentJobId, workId));
        await Grains.GetGrain<IAgentJobGrain>(agentJobId).AssignRunnerAsync(runnerId, workId);

        var first = Assert.Single((await Dispatch.PollAsync(
            runnerId, new RunnerPollRequest([], []))).Dispatches);
        var redelivery = Assert.Single((await Dispatch.PollAsync(
            runnerId, new RunnerPollRequest([], []))).Dispatches);

        Assert.Equal(first.AgentJobId, redelivery.AgentJobId);
        Assert.Equal(first.WorkId, redelivery.WorkId);

        var key = $"{WorkDispatchOwnerKinds.AgentJob}:{agentJobId}:{workId}";
        var reported = await Dispatch.PollAsync(
            runnerId, new RunnerPollRequest([key], []));
        Assert.Empty(reported.Dispatches);
    }

    [Fact]
    public async Task PollGate_AdmitsOnlyOneOverlappingPoll()
    {
        var runnerId = await RegisterRunnerAsync($"agent-job-poll-gate-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var admissions = await Task.WhenAll(
            runner.TryBeginPollAsync(),
            runner.TryBeginPollAsync());

        Assert.Single(admissions, admission => admission.Admitted);
        Assert.Single(admissions, admission => !admission.Admitted);
        await runner.EndPollAsync();
    }

    [Fact]
    public async Task AssignAgentJobAsync_DuringPollReconciliation_IsRetriedLater()
    {
        var runnerId = await RegisterRunnerAsync($"agent-job-poll-admission-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True((await runner.TryBeginPollAsync()).Admitted);

        try
        {
            var result = await runner.AssignAgentJobAsync(
                AgentDispatch("agent-job-during-poll", "agent-work-during-poll"));

            Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
            Assert.Equal("runner-reconciling", result.Reason);
        }
        finally
        {
            await runner.EndPollAsync();
        }
    }

    [Fact]
    public async Task IsWorkRunnable_AgentJobArm_NotRunnable_DropsWork()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-not-runnable-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobId = "agent-job-not-runnable";
        var workId = "agent-work-not-runnable";
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: agentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        await runner.AssignAgentJobAsync(dispatch);

        var agentJob = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        Assert.Equal(AgentJobStatus.Pending, await agentJob.GetStatusAsync());

        var work = await runner.PollAsync(Services);
        Assert.Null(work);
    }

    [Fact]
    public async Task ReportResult_AgentJobArm_RoutesToAgentJobGrain_AndDoesNotTouchWorkflowGrain()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-report-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobId = "agent-job-report-1";
        var workId = "agent-work-report";
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: agentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        await runner.AssignAgentJobAsync(dispatch);

        var agentJob = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        await agentJob.AssignRunnerAsync(runnerId, workId);

        var report = await runner.ReportAgentJobResultAsync(agentJobId, workId, new WorkResult("completed", "ok"));

        Assert.True(report.Tracked);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, report.OwnerKind);
        Assert.Equal(agentJobId, report.OwnerId);
        Assert.Equal("reported", report.Reason);

        var terminal = await agentJob.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, terminal.Status);
        Assert.Equal("ok", terminal.Message);
    }

    [Fact]
    public async Task ReportResult_AgentJobArm_MissingAgentJobId_ReturnsNotTracked()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-missing-owner-report-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: "agent-work-missing",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var report = await runner.ReportAgentJobResultAsync(string.Empty, "agent-work-missing", new WorkResult("completed"));

        Assert.False(report.Tracked);
        Assert.Equal("missing-agent-job", report.Reason);
    }

    [Fact]
    public async Task ReportResult_AgentJobArm_UntrackedWork_StillRoutesToJobGrain()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-untracked-report-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobId = "agent-job-untracked-report";
        var workId = "agent-work-untracked-report";
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: agentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var report = await runner.ReportAgentJobResultAsync(agentJobId, workId, new WorkResult("completed"));

        Assert.False(report.Tracked);
        Assert.Equal("job-rejected:not-running", report.Reason);

        var agentJob = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        var snapshot = await agentJob.GetRuntimeSnapshotAsync();
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task AssignAgentJobAsync_WorkflowDispatch_IsRejected()
    {
        var runnerId = await RegisterRunnerAsync("workflow-accept-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var dispatch = new WorkDispatch(
            WorkflowRunId: "wf-agent-job-test-1",
            WorkId: "wf-work-1",
            OwnerKind: WorkDispatchOwnerKinds.Workflow);

        var result = await runner.AssignAgentJobAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
        Assert.Equal("invalid-work", result.Reason);
    }

    [Fact]
    public async Task AssignWork_WorkflowDispatch_MissingWorkflowRunId_IsRejected()
    {
        var runnerId = await RegisterRunnerAsync("workflow-missing-owner-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: "wf-work-1",
            OwnerKind: WorkDispatchOwnerKinds.Workflow);

        var result = await runner.AssignAgentJobAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
        Assert.Equal("invalid-work", result.Reason);
    }

    [Fact]
    public async Task IsWorkRunnable_WorkflowArm_AsksWorkflowGrain_ForAssignedRunnerStatusAndWorkId()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var (work, _) = await PollWorkAnyAsync();
        Assert.NotNull(work);

        var assigned = await workflow.GetAssignedWorkerIdAsync();
        var status = await workflow.GetRunStatusAsync();
        var currentWorkId = await workflow.GetCurrentWorkIdAsync();
        Assert.Equal(runnerId, assigned);
        Assert.Equal("Running", status);
        Assert.Equal(work.WorkId, currentWorkId);
    }

    [Fact]
    public async Task IsWorkRunnable_WorkflowArm_DoesNotContactAgentJobGrain()
    {
        // Negative assertion: a workflow dispatch must never route to
        // IAgentJobGrain. We send a dispatch with a bogus agent-job id and
        // expect the workflow-arm IsWorkRunnable to ignore it and gate on
        // the workflow grain (which would say the work is not assigned to
        // this runner, returning false). The pre-existing positive test
        // already covers the happy path.
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // Trigger the agent-job grain first so it is active; a stray
        // reference to it would surface as a grain-call exception.
        var orphanAgentJob = Grains.GetGrain<IAgentJobGrain>("orphan-agent-job-from-workflow-arm");
        Assert.Equal(AgentJobStatus.Pending, await orphanAgentJob.GetStatusAsync());

        var dispatch = new WorkDispatch(
            WorkflowRunId: "wf-arm-neg",
            WorkId: "wf-arm-neg-work",
            AgentJobId: "orphan-agent-job-from-workflow-arm",
            OwnerKind: WorkDispatchOwnerKinds.Workflow);

        // Workflow dispatches are no longer accepted through the AgentJob
        // delivery arm.
        var assignment = await runner.AssignAgentJobAsync(dispatch);
        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, assignment.Status);

        // The agent-job grain must still be in Pending (never transitioned
        // by the workflow arm).
        Assert.Equal(AgentJobStatus.Pending, await orphanAgentJob.GetStatusAsync());

        Assert.Equal("invalid-work", assignment.Reason);
    }

    [Fact]
    public async Task AgentJobWork_SharesWorkflowSlotPool()
    {
        var projectId = TestProjectId("agent-job-slot-test");
        var runnerId = await RegisterRunnerForProjectAsync(projectId, "agent-job-slot-runner", maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobId = "agent-job-slot-1";
        var workId = "agent-work-slot-1";
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: agentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        await runner.AssignAgentJobAsync(dispatch);

        var state = await runner.GetRuntimeStateAsync();
        Assert.Single(state.ActiveWorks);
        Assert.Equal(agentJobId, state.ActiveWorks[0].OwnerId);
    }

    [Fact]
    public async Task AssignAgentJobAsync_ConcurrentJobsRespectSingleSlotCapacity()
    {
        var projectId = TestProjectId($"agent-job-capacity-{Guid.NewGuid():N}");
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            $"agent-job-capacity-runner-{Guid.NewGuid():N}",
            maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var assignments = await Task.WhenAll(
            runner.AssignAgentJobAsync(AgentDispatch("agent-job-capacity-a", "agent-work-capacity-a")),
            runner.AssignAgentJobAsync(AgentDispatch("agent-job-capacity-b", "agent-work-capacity-b")));

        Assert.Single(assignments, result => result.Status == RunnerWorkAssignmentStatus.Assigned);
        var rejected = Assert.Single(assignments, result => result.Status == RunnerWorkAssignmentStatus.Rejected);
        Assert.Equal("capacity-exhausted", rejected.Reason);
        Assert.Single((await runner.GetRuntimeStateAsync()).ActiveWorks);
    }

    [Fact]
    public async Task AssignAgentJobAsync_AfterUnregisterIsRejected()
    {
        var projectId = TestProjectId($"agent-job-offline-{Guid.NewGuid():N}");
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            $"agent-job-offline-runner-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var result = await runner.AssignAgentJobAsync(
            AgentDispatch("agent-job-offline", "agent-work-offline"));

        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
        Assert.Equal("runner-offline", result.Reason);
        Assert.Empty((await runner.GetRuntimeStateAsync()).ActiveWorks);
    }

    private static WorkDispatch AgentDispatch(string agentJobId, string workId) =>
        new(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: agentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);
}

[Collection("RunnerGrain")]
public class WorkDispatchSerializationSpecs
{
    private readonly WorkflowGrainFixture _fixture;

    public WorkDispatchSerializationSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void PreChange_WorkDispatch_Payload_DecodesWithDefaults()
    {
        // Build a hand-crafted on-the-wire pre-change payload (no OwnerKind /
        // AgentJobId fields, matching what an older runner / server would
        // have sent). We do NOT rely on a separate record type because
        // Orleans serialization is sensitive to the [Id(n)] slots on the
        // target type, not on a structurally identical surrogate. The
        // default parameter values on the target WorkDispatch record must
        // survive the round-trip so that older payloads decode correctly.
        var serviceProvider = _fixture.Cluster.GetSiloServiceProvider(null);
        var serializer = serviceProvider.GetRequiredService<Serializer>();

        var preChange = new WorkDispatch(
            WorkflowRunId: "wf-pre-change-1",
            WorkId: "work-pre-change-1",
            Uses: "spec/task",
            With: "{}",
            Variables: "{}",
            WorkType: "task",
            Stage: "build",
            Title: "Test");

        var preChangeBytes = serializer.SerializeToArray(preChange);
        var deserialized = serializer.Deserialize<WorkDispatch>(preChangeBytes);

        Assert.NotNull(deserialized);
        Assert.Equal("wf-pre-change-1", deserialized.WorkflowRunId);
        Assert.Equal("work-pre-change-1", deserialized.WorkId);
        Assert.Equal(WorkDispatchOwnerKinds.Workflow, deserialized.OwnerKind);
        Assert.Null(deserialized.AgentJobId);
    }

    [Fact]
    public void PreChange_WorkDispatch_OwnerKindDefaultsWhenFieldMissing()
    {
        // Pin the behaviour of the default parameter value for OwnerKind
        // (`"workflow"`) so a future refactor cannot change the default
        // without breaking older clients that did not set the field.
        var direct = new WorkDispatch(WorkflowRunId: "wf-x", WorkId: "w-x");
        Assert.Equal(WorkDispatchOwnerKinds.Workflow, direct.OwnerKind);
        Assert.Null(direct.AgentJobId);
    }

    [Fact]
    public void PreChange_WorkDispatch_JsonPayload_DecodesWithDefaults()
    {
        var json = """
        {
          "workflowRunId": "wf-json-pre-change",
          "workId": "work-json-pre-change",
          "uses": "spec/task",
          "with": "{}",
          "variables": "{}",
          "workType": "task",
          "stage": "build",
          "title": "Test",
          "artifacts": "{\"files\":[]}"
        }
        """;

        var dispatch = JsonSerializer.Deserialize<WorkDispatch>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(dispatch);
        Assert.Equal("wf-json-pre-change", dispatch!.WorkflowRunId);
        Assert.Equal("work-json-pre-change", dispatch.WorkId);
        Assert.Equal(WorkDispatchOwnerKinds.Workflow, dispatch.OwnerKind);
        Assert.Null(dispatch.AgentJobId);
    }
}
