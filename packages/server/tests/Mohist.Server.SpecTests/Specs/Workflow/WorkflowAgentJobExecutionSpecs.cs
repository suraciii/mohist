using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

[Collection("WorkflowExecution")]
public sealed class WorkflowAgentJobExecutionSpecs : WorkflowGrainSpecs
{
    public WorkflowAgentJobExecutionSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task WorkflowAgentAction_CreatesAgentJobAndSession_AndFinalizesAfterReactivation()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [
                new TaskDefinition(
                    "build",
                    "Build",
                    "mohist/agent",
                    new Dictionary<string, System.Text.Json.JsonElement?>
                    {
                        ["name"] = Json("mohist/builder"),
                        ["session"] = Json("build"),
                        ["prompt"] = Json("Build the change."),
                    })
            ], [])
        ]);
        var workflow = await StartWorkflowAsync(definition, $"workflow-agent-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);

        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var run = await LoadRunAsync(_workflowId!);
        var attempt = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Running, attempt.Status);
        Assert.NotNull(attempt.AgentJobId);
        Assert.NotNull(attempt.AgentSessionId);
        Assert.NotNull(attempt.AgentInvocationId);
        Assert.Null(attempt.WorkerId);

        var key = WorkflowAgentHandoffCodec.KeyFor(
            run.Metadata.ProjectId!,
            run.Id,
            attempt.Id,
            attempt.WorkId!);
        var handoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(key);
        await handoff.ActivateAsync();
        var plan = await handoff.GetPlanAsync();
        Assert.NotNull(plan);
        Assert.Equal(WorkflowAgentActivationStep.Completed, plan!.ActivationStep);
        Assert.Null(plan.ActivationError);

        var job = Grains.GetGrain<IAgentJobGrain>(attempt.AgentJobId!);
        var jobSnapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(attempt.AgentSessionId, jobSnapshot.AgentSessionId);
        Assert.Equal(run.Id, jobSnapshot.WorkflowOrigin?.WorkflowRunId);
        Assert.Equal(attempt.Id, jobSnapshot.WorkflowOrigin?.ActionAttemptId);

        await DeactivateWorkflowAsync(run.Id);
        workflow = Grains.GetGrain<IWorkflowGrain>(run.Id);

        var dispatch = await PollWorkAsync(runnerId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, dispatch.Work.OwnerKind);
        Assert.Equal(attempt.AgentJobId, dispatch.Work.AgentJobId);
        Assert.Equal(attempt.AgentSessionId, dispatch.Work.AgentSessionId);
        await ReportAsync(runnerId, dispatch.Work, "completed");

        run = await LoadRunAsync(run.Id);
        Assert.Equal(WorkflowActionAttemptStatus.Completed, run.CurrentStage().Tasks.Single().Status);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task WorkflowAgentAction_ReusesNamedSessionAcrossAgentJobs()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [
                AgentTask("first", "First", "delivery"),
                AgentTask("second", "Second", "delivery"),
            ], [])
        ]);
        var workflow = await StartWorkflowAsync(definition, $"workflow-agent-reuse-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);

        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var first = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, first.Work, "completed");

        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var second = await PollWorkAsync(runnerId);

        Assert.NotEqual(first.Work.AgentJobId, second.Work.AgentJobId);
        Assert.Equal(first.Work.AgentSessionId, second.Work.AgentSessionId);
        await ReportAsync(runnerId, second.Work, "completed");
        Assert.Equal(WorkflowRunStatus.Completed, (await LoadRunAsync(_workflowId!)).Status);
    }

    [Fact]
    public async Task WorkflowAgentAction_MissingAgent_DurablyFailsAttempt()
    {
        var missingAgent = $"missing-agent-{Guid.NewGuid():N}";
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [
                new TaskDefinition(
                    "build",
                    "Build",
                    "mohist/agent",
                    new Dictionary<string, System.Text.Json.JsonElement?>
                    {
                        ["name"] = Json(missingAgent),
                        ["prompt"] = Json("Build the change."),
                    })
            ], [])
        ]);
        var workflow = await StartWorkflowAsync(definition, $"workflow-agent-missing-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);

        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));

        var run = await LoadRunAsync(_workflowId!);
        var attempt = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, attempt.Status);
        Assert.Equal("agent_not_found", attempt.Error?.Code);
        Assert.Null(attempt.AgentJobId);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task WorkflowAgentAction_AppliesSetVarsFromTerminalOutput()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [
                new TaskDefinition(
                    "build",
                    "Build",
                    "mohist/agent",
                    new Dictionary<string, System.Text.Json.JsonElement?>
                    {
                        ["name"] = Json("mohist/builder"),
                        ["prompt"] = Json("Build the change."),
                    },
                    SetVars: new Dictionary<string, string> { ["revision"] = "output.result.revision" })
            ], [])
        ]);
        var workflow = await StartWorkflowAsync(definition, $"workflow-agent-set-vars-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);

        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var dispatch = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, dispatch.Work, new WorkResult(
            "completed",
            Output: System.Text.Json.JsonSerializer.SerializeToElement(new { result = new { revision = "abc123" } })));

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var variables = await scope.ServiceProvider.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(_workflowId!);
        Assert.Equal("abc123", variables.Vars!.Value.GetProperty("revision").GetString());
        Assert.Equal(WorkflowRunStatus.Completed, (await LoadRunAsync(_workflowId!)).Status);
    }

    [Fact]
    public async Task BuiltInLocalProfile_CompletesPlanThroughIntegrate_WithAgentAndMechanicalOwnership()
    {
        await ClearGlobalRunnerRegistryAsync();
        var workflow = await StartWorkflowAsync(
            Mohist.Server.Workflow.Services.WorkflowProfileCatalog.Definition,
            $"workflow-agent-builtin-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);
        var agentJobs = new List<string>();
        var mechanicalWorks = new List<string>();

        for (var step = 0; step < 80; step++)
        {
            var run = await LoadRunAsync(_workflowId!);
            if (run.Status == WorkflowRunStatus.Completed)
                break;
            if (run.CurrentStage().Status == StageRunStatus.AwaitingApproval)
            {
                await workflow.ApproveAsync();
                continue;
            }

            var dispatched = await PollWorkAsync(runnerId);
            if (string.Equals(dispatched.Work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            {
                Assert.NotNull(dispatched.Work.AgentJobId);
                Assert.NotNull(dispatched.Work.AgentSessionId);
                Assert.NotNull(dispatched.Work.ActionAttemptId);
                agentJobs.Add(dispatched.Work.AgentJobId!);
                await ReportAsync(runnerId, dispatched.Work, "completed");
            }
            else if (string.Equals(dispatched.Work.WorkType, "checks", StringComparison.Ordinal))
            {
                var current = (await LoadRunAsync(_workflowId!)).CurrentStage();
                await ReportChecksPassAsync(runnerId, dispatched.Work, current.Checks.Select(check => check.Name).ToArray());
            }
            else
            {
                mechanicalWorks.Add(dispatched.Work.WorkId);
                await ReportAsync(runnerId, dispatched.Work, "completed");
            }
        }

        var completed = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
        Assert.Equal(["plan", "build", "check", "integrate"], completed.Stages.Select(stage => stage.Id));
        Assert.Contains(completed.Stages.SelectMany(stage => stage.Tasks),
            attempt => attempt.Uses == "mohist/agent" && attempt.AgentJobId is not null && attempt.AgentSessionId is not null);
        Assert.All(completed.Stages.SelectMany(stage => stage.Tasks).Where(attempt => attempt.Uses == "mohist/agent"),
            attempt => Assert.Null(attempt.WorkerId));
        Assert.True(agentJobs.Count >= 2);
        Assert.NotEmpty(mechanicalWorks);
    }

    private static TaskDefinition AgentTask(string id, string title, string session) => new(
        id,
        title,
        "mohist/agent",
        new Dictionary<string, System.Text.Json.JsonElement?>
        {
            ["name"] = Json("mohist/builder"),
            ["session"] = Json(session),
            ["prompt"] = Json(title),
        });

    private static System.Text.Json.JsonElement Json(string value) =>
        System.Text.Json.JsonSerializer.SerializeToElement(value);
}
