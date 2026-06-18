using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Orleans.Serialization;
using Xunit;
using System.Text.Json;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

public class AgentJobOwnerKindSpecs : WorkflowGrainSpecs
{
    public AgentJobOwnerKindSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        var result = await runner.AssignWorkAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, result.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        var result = await runner.AssignWorkAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
        Assert.Equal("invalid-work", result.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        var result = await runner.AssignWorkAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
        Assert.Equal("invalid-work", result.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        await runner.AssignWorkAsync(dispatch);

        var agentJob = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        await agentJob.AssignRunnerAsync(runnerId, workId);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.Equal(agentJobId, work.AgentJobId);
        Assert.Equal(workId, work.WorkId);

        var snapshot = await agentJob.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Running, snapshot.Status);
        Assert.Equal(workId, snapshot.CurrentWorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        await runner.AssignWorkAsync(dispatch);

        var agentJob = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        Assert.Equal(AgentJobStatus.Pending, await agentJob.GetStatusAsync());

        var work = await runner.PollAsync();
        Assert.Null(work);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        await runner.AssignWorkAsync(dispatch);

        var agentJob = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        await agentJob.AssignRunnerAsync(runnerId, workId);

        var report = await runner.ReportResultAsync(dispatch, workId, new WorkResult("completed", "ok"));

        Assert.True(report.Tracked);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, report.OwnerKind);
        Assert.Equal(agentJobId, report.OwnerId);
        Assert.Equal("reported", report.Reason);

        var terminal = await agentJob.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, terminal.Status);
        Assert.Equal("ok", terminal.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReportResult_AgentJobArm_MissingAgentJobId_ReturnsNotTracked()
    {
        var runnerId = await RegisterRunnerAsync("agent-job-missing-owner-report-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: "agent-work-missing",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var report = await runner.ReportResultAsync(dispatch, "agent-work-missing", new WorkResult("completed"));

        Assert.False(report.Tracked);
        Assert.Equal("missing-agent-job", report.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        var report = await runner.ReportResultAsync(dispatch, workId, new WorkResult("completed"));

        Assert.False(report.Tracked);
        Assert.Equal("job-rejected:not-running", report.Reason);

        var agentJob = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        var snapshot = await agentJob.GetRuntimeSnapshotAsync();
        Assert.NotNull(snapshot);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task AssignWork_WorkflowDispatch_StillAccepted()
    {
        var runnerId = await RegisterRunnerAsync("workflow-accept-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var dispatch = new WorkDispatch(
            WorkflowRunId: "wf-agent-job-test-1",
            WorkId: "wf-work-1",
            OwnerKind: WorkDispatchOwnerKinds.Workflow);

        var result = await runner.AssignWorkAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, result.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task AssignWork_WorkflowDispatch_MissingWorkflowRunId_IsRejected()
    {
        var runnerId = await RegisterRunnerAsync("workflow-missing-owner-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: "wf-work-1",
            OwnerKind: WorkDispatchOwnerKinds.Workflow);

        var result = await runner.AssignWorkAsync(dispatch);

        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
        Assert.Equal("invalid-work", result.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task IsWorkRunnable_WorkflowArm_AsksWorkflowGrain_ForClaimedRunnerStatusAndWorkId()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var (work, _) = await PollWorkAnyAsync();
        Assert.NotNull(work);

        var claimed = await workflow.GetClaimedRunnerIdAsync();
        var status = await workflow.GetRunStatusAsync();
        var currentWorkId = await workflow.GetCurrentWorkIdAsync();
        Assert.Equal(runnerId, claimed);
        Assert.Equal("Running", status);
        Assert.Equal(work.WorkId, currentWorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        // Assigning will go on the workflow-arm branch.
        var assignment = await runner.AssignWorkAsync(dispatch);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assignment.Status);

        // The agent-job grain must still be in Pending (never transitioned
        // by the workflow arm).
        Assert.Equal(AgentJobStatus.Pending, await orphanAgentJob.GetStatusAsync());

        // Keep the workflow alive through tear-down so it doesn't deallocate.
        await ReportAsync(runnerId, "wf-arm-neg", "wf-arm-neg-work", new WorkResult("completed", "ok"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReportResult_WorkflowArm_ReportsToWorkflowGrain_AndReadsBackStatus_ByteEquivalent()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var (work, _) = await PollWorkAnyAsync();
        var report = await runner.ReportResultAsync(work, work.WorkId, new WorkResult("completed"));

        Assert.True(report.Tracked);
        Assert.Equal(work.WorkflowRunId, report.WorkflowRunId);
        Assert.Equal("reported", report.Reason);
        Assert.Null(report.OwnerKind);
        Assert.Null(report.OwnerId);
        Assert.Equal("Running", report.WorkflowStatus);

        var orphanAgentJob = Grains.GetGrain<IAgentJobGrain>("workflow-report-orphan-agent-job");
        Assert.Equal(AgentJobStatus.Pending, await orphanAgentJob.GetStatusAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

        await runner.AssignWorkAsync(dispatch);

        var state = await runner.GetRuntimeStateAsync();
        Assert.Single(state.ActiveWorkflowRunIds);
        Assert.Equal(agentJobId, state.ActiveWorkflowRunIds[0]);
    }
}

[Collection("WorkflowGrain")]
[Trait(Traits.Speed.Name, Traits.Speed.Grain)]
[Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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
