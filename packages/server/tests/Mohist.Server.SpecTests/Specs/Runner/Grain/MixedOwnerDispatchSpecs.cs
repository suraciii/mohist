using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public sealed class MixedOwnerDispatchSpecs : Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainSpecs
{
    public MixedOwnerDispatchSpecs(Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainFixture fixture) : base(fixture) { }

    private DispatchService Dispatch => _fixture.Cluster.GetSiloServiceProvider(null)
        .GetRequiredService<IServiceScopeFactory>().CreateScope()
        .ServiceProvider.GetRequiredService<DispatchService>();

    [Fact]
    public async Task Poll_ReturnsAdmittedAgentJobFromOwnerLedger()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-poll-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"agent-poll-runner-{Guid.NewGuid():N}");
        var jobId = $"agent-poll-job-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobId);

        await job.SubmitAsync(new AgentJobInput(
            "poll this job",
            WorkspacePath: "/tmp/agent-poll",
            ProjectId: projectId,
            AgentId: "agent-test"));

        var pending = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, pending.Status);
        Assert.Equal(runnerId, pending.RunnerId);

        using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            var ledger = await store.LoadLedgerAsync(jobId);
            Assert.NotNull(ledger);
            Assert.NotNull(ledger!.DispatchJson);
            Assert.Equal(runnerId, ledger.AssignedRunnerId);

        }

        var response = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));

        var dispatch = Assert.Single(response.Dispatches);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, dispatch.OwnerKind);
        Assert.Equal(jobId, dispatch.AgentJobId);
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }

    [Fact]
    public async Task ConditionalAgentJobClaim_RejectsStaleCapabilityRevisionAndLeavesJobPending()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-capability-fence-project-{Guid.NewGuid():N}";
        var runnerId = $"agent-capability-fence-runner-{Guid.NewGuid():N}";
        var jobId = $"agent-capability-fence-job-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "capability-fence-host",
            projectId,
            ConnectionGeneration: "connection-1",
            RuntimeCatalogs: new Dictionary<string, RuntimeCatalogEntry>
            {
                ["pi"] = new(
                    Models: ["openai/model"],
                    Variants: new Dictionary<string, string[]>
                    {
                        ["openai/model"] = ["balanced", "high"],
                    },
                    SupportsReasoningEffort: true,
                    Complete: true,
                    CapabilityRevision: "catalog-rev-1",
                    ReasoningEfforts: new Dictionary<string, string[]>
                    {
                        ["openai/model"] = ["high"],
                    }),
            }));
        await runner.ObserveRuntimeReadinessAsync(
            "connection-1",
            [new RuntimeReadinessWitness("pi", Ready: true, Generation: 1)]);

        var job = Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "reject stale capability",
            Model: "openai/model",
            WorkspacePath: "/tmp/agent-capability-fence",
            ProjectId: projectId,
            Runtime: "pi",
            AgentId: "agent-test",
            AgentConfig: JsonDocument.Parse(
                "{\"type\":\"pi\",\"model\":\"openai/model\",\"reasoningEffort\":\"high\",\"variant\":\"balanced\"}").RootElement.Clone(),
            Variant: "balanced",
            ReasoningEffort: "high"));

        var pending = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, pending.Status);
        Assert.Equal(runnerId, pending.RunnerId);
        Assert.False(string.IsNullOrWhiteSpace(pending.CurrentWorkId));

        var expectation = new CapabilityClaimExpectation(
            WorkDispatchOwnerKinds.AgentJob,
            jobId,
            pending.CurrentWorkId!,
            Runtime: "pi",
            Model: "openai/model",
            ReasoningEffort: "high",
            Variant: "balanced",
            CapabilityRevision: "catalog-rev-stale",
            RuntimeGeneration: 1,
            ConnectionGeneration: "connection-1");

        Assert.Null(await runner.TryClaimAgentJobAsync(jobId, projectId, expectation));
        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());
    }

    [Fact]
    public async Task Poll_RedeliversAgentJobAfterOwnerActivation()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-recovery-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"agent-recovery-runner-{Guid.NewGuid():N}");
        var jobId = $"agent-recovery-job-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobId);

        await job.SubmitAsync(new AgentJobInput(
            "recover this job",
            WorkspacePath: "/tmp/agent-recovery",
            ProjectId: projectId,
            AgentId: "agent-test"));

        var first = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        await TestLifecycle.Deactivate(job);

        var redelivery = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        Assert.Equal(first.AgentJobId, redelivery.AgentJobId);
        Assert.Equal(first.WorkId, redelivery.WorkId);
    }

    [Fact]
    public async Task Poll_RedeliversRecoveryBeforeClaimingPendingWorkflow()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"mixed-recovery-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            $"mixed-recovery-runner-{Guid.NewGuid():N}",
            maxWorkflowSlots: 2);
        var jobId = $"mixed-recovery-job-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            "recover before workflow",
            WorkspacePath: "/tmp/mixed-recovery",
            ProjectId: projectId,
            AgentId: "agent-test"));

        var first = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        var workflowId = $"mixed-recovery-workflow-{Guid.NewGuid():N}";
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []), projectId);
        await workflow.StartAsync(TestInput(projectId));

        var response = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));

        Assert.Equal(2, response.Dispatches.Count);
        Assert.Equal(first.AgentJobId, response.Dispatches[0].AgentJobId);
        Assert.Equal(workflowId, response.Dispatches[1].WorkflowRunId);
    }

    [Fact]
    public async Task Poll_AssignedCandidatesMergeByReadySinceAcrossOwners()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"mixed-order-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            $"mixed-order-runner-{Guid.NewGuid():N}",
            maxWorkflowSlots: 2);
        var workflowId = $"mixed-order-workflow-{Guid.NewGuid():N}";
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []), projectId);
        await workflow.StartAsync(TestInput(projectId));
        await workflow.AssignWorkerAsync(runnerId);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var jobId = $"mixed-order-job-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            "serve after workflow",
            WorkspacePath: "/tmp/mixed-order",
            ProjectId: projectId,
            AgentId: "agent-test"));

        var response = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));

        Assert.Equal(2, response.Dispatches.Count);
        Assert.Equal(workflowId, response.Dispatches[0].WorkflowRunId);
        Assert.Equal(jobId, response.Dispatches[1].AgentJobId);
    }

    [Fact]
    public async Task Poll_CapacityRaceLeavesAgentJobPending()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"mixed-capacity-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            $"mixed-capacity-runner-{Guid.NewGuid():N}",
            maxWorkflowSlots: 1);
        var jobId = $"mixed-capacity-job-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            "wait for capacity",
            WorkspacePath: "/tmp/mixed-capacity",
            ProjectId: projectId,
            AgentId: "agent-test"));

        var workflowId = $"mixed-capacity-workflow-{Guid.NewGuid():N}";
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []), projectId);
        await workflow.StartAsync(TestInput(projectId));
        await workflow.AssignWorkerAsync(runnerId);
        Assert.NotNull(await workflow.ClaimNextAsync(runnerId));

        var response = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));

        Assert.Single(response.Dispatches);
        Assert.Equal(workflowId, response.Dispatches[0].WorkflowRunId);
        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());
    }

    [Fact]
    public async Task Poll_CapacityRace_LeavesAgentJobPendingWhenCapacityRemainsFull()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"mixed-capacity-timeout-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            $"mixed-capacity-timeout-runner-{Guid.NewGuid():N}",
            maxWorkflowSlots: 1);
        var job = Grains.GetGrain<IAgentJobGrain>($"mixed-capacity-timeout-job-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput(
            "expire after capacity race",
            WorkspacePath: "/tmp/mixed-capacity-timeout",
            ProjectId: projectId,
            AgentId: "agent-test"));

        var workflowId = $"mixed-capacity-timeout-workflow-{Guid.NewGuid():N}";
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []), projectId);
        await workflow.StartAsync(TestInput(projectId));
        await workflow.AssignWorkerAsync(runnerId);
        Assert.NotNull(await workflow.ClaimNextAsync(runnerId));
        await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await job.ReceiveReminder("agent-job-recovery", new TickStatus(now, TimeSpan.FromSeconds(1), now));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Pending, terminal.Status);
        Assert.Null(terminal.FailureReason);
    }
}
