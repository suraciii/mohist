using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Subscriptions;
using Mohist.Workflow.Definition;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Mohist.Server.Tests.Workflow;

[Collection("WorkflowExecution")]
[Trait("level", "L1")]
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
            run.CurrentStage().Id,
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
    public async Task WorkflowAgentAction_ReactivationResumesLegacyHandoffForAlreadyTerminalJob()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [AgentTask("build", "Build the change", "delivery")], [])
        ]);
        var workflow = await StartWorkflowAsync(
            definition,
            $"workflow-agent-legacy-handoff-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);

        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var run = await LoadRunAsync(_workflowId!);
        var attempt = Assert.Single(run.CurrentStage().Tasks);
        var stageKey = WorkflowAgentHandoffCodec.KeyFor(
            run.Metadata.ProjectId!,
            run.Id,
            run.CurrentStage().Id,
            attempt.Id,
            attempt.WorkId!);
        var stageHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(stageKey);
        await stageHandoff.ActivateAsync();
        var dispatch = (await PollWorkAsync(runnerId)).Work;
        var job = Grains.GetGrain<IAgentJobGrain>(dispatch.AgentJobId!);
        var runtimeSessionId = $"runtime-{dispatch.WorkId}";
        Assert.True(await job.RecordRuntimeSessionBindingAsync(
            runnerId,
            dispatch.WorkId,
            dispatch.AgentSessionId!,
            runtimeSessionId));
        var result = await job.ReportResultAsync(
            runnerId,
            dispatch.WorkId,
            new WorkResult(
                "completed",
                AgentSessionId: dispatch.AgentSessionId,
                AgentTurnId: dispatch.InitialTurnId,
                Runtime: dispatch.AgentDefinition?.Runtime,
                RuntimeSessionId: runtimeSessionId));
        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(AgentJobStatus.Completed, (await job.GetRuntimeSnapshotAsync()).Status);
        Assert.Equal(WorkflowRunStatus.Running, (await LoadRunAsync(run.Id)).Status);

        await TestLifecycle.DeactivateAndWait(stageHandoff, Grains);
        var storage = Services.GetRequiredService<IGrainStorage>();
        var stored = new GrainState<WorkflowAgentHandoffState>();
        await storage.ReadStateAsync("workflow-agent-handoff", stageHandoff.GetGrainId(), stored);
        Assert.NotNull(stored.State.Plan);
        var legacyKey = WorkflowAgentHandoffCodec.LegacyKeyFor(
            run.Metadata.ProjectId!,
            run.Id,
            attempt.Id,
            attempt.WorkId!);
        var legacyHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(legacyKey);
        await storage.WriteStateAsync(
            "workflow-agent-handoff",
            legacyHandoff.GetGrainId(),
            new GrainState<WorkflowAgentHandoffState> { State = stored.State });
        await storage.WriteStateAsync(
            "workflow-agent-handoff",
            stageHandoff.GetGrainId(),
            new GrainState<WorkflowAgentHandoffState>
            {
                State = new WorkflowAgentHandoffState(),
                ETag = stored.ETag,
            });

        await DeactivateWorkflowAsync(run.Id);
        workflow = Grains.GetGrain<IWorkflowGrain>(run.Id);
        Assert.Equal("Running", await workflow.GetRunStatusAsync());

        await Services.GetRequiredService<IEventDispatcher>().DrainAsync();
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
    public async Task WorkflowAgentAction_SameNamedSessionAndTaskIdAcrossStages_UsesDistinctLaunchIdentities()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("plan", [AgentTask("shared", "Plan the change", "delivery")], []),
            new StageDefinition("check", [AgentTask("shared", "Check the change", "delivery")], []),
        ]);
        var workflow = await StartWorkflowAsync(
            definition,
            $"workflow-agent-cross-stage-session-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);

        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var firstDispatch = (await PollWorkAsync(runnerId)).Work;
        var firstRun = await LoadRunAsync(_workflowId!);
        var firstAttempt = Assert.Single(firstRun.Stages.Single(stage => stage.Id == "plan").Tasks);
        await ReportAsync(runnerId, firstDispatch, "completed");

        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var secondDispatch = (await PollWorkAsync(runnerId)).Work;
        var secondRun = await LoadRunAsync(_workflowId!);
        var secondAttempt = Assert.Single(secondRun.Stages.Single(stage => stage.Id == "check").Tasks);

        Assert.Equal(firstAttempt.DefinitionId, secondAttempt.DefinitionId);
        Assert.Equal(firstDispatch.AgentSessionId, secondDispatch.AgentSessionId);
        Assert.NotEqual(firstAttempt.AgentInvocationId, secondAttempt.AgentInvocationId);
        Assert.NotEqual(firstDispatch.AgentJobId, secondDispatch.AgentJobId);
        Assert.NotEqual(firstDispatch.InitialInputId, secondDispatch.InitialInputId);
        Assert.NotEqual(firstDispatch.InitialTurnId, secondDispatch.InitialTurnId);
        await using (var scope = Services.CreateAsyncScope())
        {
            var session = await scope.ServiceProvider.GetRequiredService<IAgentSessionStore>()
                .LoadAsync(secondDispatch.AgentSessionId!);
            var secondInput = Assert.Single(
                session!.Status.Inputs!,
                input => string.Equals(input.Id, secondDispatch.InitialInputId, StringComparison.Ordinal));
            Assert.Equal(secondAttempt.AgentInvocationId, secondInput.IdempotencyKey);
        }

        await ReportAsync(runnerId, secondDispatch, "completed");
        Assert.Equal(WorkflowRunStatus.Completed, (await LoadRunAsync(_workflowId!)).Status);
    }

    [Fact]
    public async Task WorkflowAgentHandoffs_SameExactWorkIdentityAcrossStages_AppendsDistinctNamedSessionFollowups()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("bootstrap", [AgentTask("bootstrap", "Bootstrap", "delivery")], [])
        ]);
        var workflow = await StartWorkflowAsync(
            definition,
            $"workflow-agent-exact-cross-stage-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);
        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var bootstrapRun = await LoadRunAsync(_workflowId!);
        var bootstrapAttempt = Assert.Single(bootstrapRun.CurrentStage().Tasks);
        var bootstrapKey = WorkflowAgentHandoffCodec.KeyFor(
            bootstrapRun.Metadata.ProjectId!,
            bootstrapRun.Id,
            bootstrapRun.CurrentStage().Id,
            bootstrapAttempt.Id,
            bootstrapAttempt.WorkId!);
        var bootstrapHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(bootstrapKey);
        await bootstrapHandoff.ActivateAsync();
        var bootstrapPlan = await bootstrapHandoff.GetPlanAsync();
        Assert.NotNull(bootstrapPlan?.Invocation);
        await ReportAsync(runnerId, (await PollWorkAsync(runnerId)).Work, "completed");

        const string sharedIdentity = "apply-feedback.1";
        var planCommand = bootstrapPlan!.Command with
        {
            CommandId = sharedIdentity,
            ActionAttemptId = sharedIdentity,
            Prompt = "Apply the feedback",
            ReuseSessionId = bootstrapPlan.Invocation!.SessionId,
            Completion = bootstrapPlan.Command.Completion! with
            {
                WorkId = sharedIdentity,
                Stage = "plan",
            },
        };
        var checkCommand = planCommand with
        {
            Completion = planCommand.Completion! with { Stage = "check" },
        };

        var planHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(planCommand));
        var preparedPlan = await planHandoff.PrepareAsync(planCommand);
        await planHandoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            sharedIdentity,
            WorkflowAgentHandoffCodec.Fingerprint(planCommand)));
        var activatedPlan = await planHandoff.ActivateAsync();

        var checkHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(checkCommand));
        var preparedCheck = await checkHandoff.PrepareAsync(checkCommand);
        await checkHandoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            sharedIdentity,
            WorkflowAgentHandoffCodec.Fingerprint(checkCommand)));
        var activatedCheck = await checkHandoff.ActivateAsync();

        var activatedPlanState = await planHandoff.GetPlanAsync();
        var activatedCheckState = await checkHandoff.GetPlanAsync();
        Assert.Null(activatedPlanState!.ActivationError);
        Assert.Null(activatedCheckState!.ActivationError);
        Assert.Equal(WorkflowAgentActivationStep.Completed, activatedPlanState.ActivationStep);
        Assert.Equal(WorkflowAgentActivationStep.Completed, activatedCheckState.ActivationStep);
        Assert.Equal(bootstrapPlan.Invocation.SessionId, preparedPlan.Invocation!.SessionId);
        Assert.Equal(preparedPlan.Invocation.SessionId, preparedCheck.Invocation!.SessionId);
        Assert.NotEqual(preparedPlan.Invocation.InvocationId, preparedCheck.Invocation.InvocationId);
        Assert.NotEqual(preparedPlan.Invocation.JobKey, preparedCheck.Invocation.JobKey);
        Assert.NotEqual(preparedPlan.Invocation.InputId, preparedCheck.Invocation.InputId);
        Assert.NotEqual(preparedPlan.Invocation.TurnId, preparedCheck.Invocation.TurnId);
        Assert.Equal(preparedPlan.Invocation, activatedPlan.Invocation);
        Assert.Equal(preparedCheck.Invocation, activatedCheck.Invocation);

        var planReplay = await planHandoff.PrepareAsync(planCommand);
        var checkReplay = await checkHandoff.PrepareAsync(checkCommand);
        Assert.True(planReplay.AlreadyPersisted);
        Assert.True(checkReplay.AlreadyPersisted);
        Assert.Equal(preparedPlan.Invocation, planReplay.Invocation);
        Assert.Equal(preparedCheck.Invocation, checkReplay.Invocation);

        await using var scope = Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<IAgentSessionStore>()
            .LoadAsync(preparedPlan.Invocation.SessionId);
        var planInput = Assert.Single(
            session!.Status.Inputs!,
            input => string.Equals(input.Id, preparedPlan.Invocation.InputId, StringComparison.Ordinal));
        var checkInput = Assert.Single(
            session.Status.Inputs!,
            input => string.Equals(input.Id, preparedCheck.Invocation.InputId, StringComparison.Ordinal));
        Assert.Equal(preparedPlan.Invocation.InvocationId, planInput.IdempotencyKey);
        Assert.Equal(preparedCheck.Invocation.InvocationId, checkInput.IdempotencyKey);
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
    public async Task WorkflowAgentAction_SetVarsWithPullRequestCarrier_RecordsNestedIdentity()
    {
        await ClearBacklogAsync();
        _workflowId = $"workflow-agent-set-vars-pr-{Guid.NewGuid():N}";
        var projectId = TestProjectId(_workflowId);
        _runnerId = await RegisterRunnerAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId);
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
                    SetVars: new Dictionary<string, string>
                    {
                        ["github.pr.number"] = "output.result.number",
                    })
            ], [])
        ]);
        await SeedWorkflowTemplateAsync(_workflowId, definition, projectId);
        var repository = new WorkflowRepositoryContext(
            "web",
            "https://github.com/octocat/hello-world.git",
            "master");
        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(projectId, 1, null),
            new WorkflowStartSnapshot(repository, null));
        Assert.Equal(WorkflowAssignmentStatus.Assigned,
            (await workflow.AssignWorkerAsync(_runnerId)).Status);

        Assert.Null(await workflow.ClaimNextAsync(_runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var dispatch = await PollWorkAsync(_runnerId);
        await ReportAsync(_runnerId, dispatch.Work, new WorkResult(
            "completed",
            Output: System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                result = new { number = 42 },
            })));

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var variables = await scope.ServiceProvider.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(_workflowId);
        Assert.Equal(42, variables.Vars!.Value
            .GetProperty("github")
            .GetProperty("pr")
            .GetProperty("number")
            .GetInt32());

        var run = await LoadRunAsync(_workflowId);
        Assert.Equal(42, run.PullRequestIdentity!.Number);
        Assert.Equal(repository, run.PullRequestIdentity.Repository);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task WorkflowAgentAction_SetVarsDirectNull_DeletesExistingTarget()
    {
        await ClearBacklogAsync();
        _workflowId = $"workflow-agent-set-vars-direct-null-{Guid.NewGuid():N}";
        var projectId = TestProjectId(_workflowId);
        _runnerId = await RegisterRunnerAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId);
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
                    SetVars: new Dictionary<string, string>
                    {
                        ["settings"] = "output.result",
                    })
            ], [])
        ]);
        await SeedWorkflowTemplateAsync(_workflowId, definition, projectId);
        var repository = new WorkflowRepositoryContext(
            "web",
            "https://github.com/octocat/hello-world.git",
            "master");
        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(projectId, 1, null),
            new WorkflowStartSnapshot(repository, null));
        Assert.Equal(WorkflowAssignmentStatus.Assigned,
            (await workflow.AssignWorkerAsync(_runnerId)).Status);
        await workflow.PatchVariablesAsync(new VariableBundle(Vars: System.Text.Json.JsonSerializer.SerializeToElement(
            new { settings = new { old = 1, keep = 2 } })));

        Assert.Null(await workflow.ClaimNextAsync(_runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var dispatch = await PollWorkAsync(_runnerId);
        await ReportAsync(_runnerId, dispatch.Work, new WorkResult(
            "completed",
            Output: System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                result = (string?)null,
            })));

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var variables = await scope.ServiceProvider.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(_workflowId!);
        Assert.False(variables.Vars!.Value.TryGetProperty("settings", out _));
        Assert.Equal(WorkflowRunStatus.Completed, (await LoadRunAsync(_workflowId!)).Status);
    }

    [Fact]
    public async Task WorkflowAgentAction_SetVarsNullLeafTarget_DeletesNestedProperty()
    {
        await ClearBacklogAsync();
        _workflowId = $"workflow-agent-set-vars-null-target-{Guid.NewGuid():N}";
        var projectId = TestProjectId(_workflowId);
        _runnerId = await RegisterRunnerAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId);
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
                    SetVars: new Dictionary<string, string>
                    {
                        ["settings.old"] = "output.result",
                    })
            ], [])
        ]);
        await SeedWorkflowTemplateAsync(_workflowId, definition, projectId);
        var repository = new WorkflowRepositoryContext(
            "web",
            "https://github.com/octocat/hello-world.git",
            "master");
        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(projectId, 1, null),
            new WorkflowStartSnapshot(repository, null));
        Assert.Equal(WorkflowAssignmentStatus.Assigned,
            (await workflow.AssignWorkerAsync(_runnerId)).Status);
        await workflow.PatchVariablesAsync(new VariableBundle(Vars: System.Text.Json.JsonSerializer.SerializeToElement(
            new { settings = new { old = 1, keep = 2 } })));

        Assert.Null(await workflow.ClaimNextAsync(_runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var dispatch = await PollWorkAsync(_runnerId);
        await ReportAsync(_runnerId, dispatch.Work, new WorkResult(
            "completed",
            Output: System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                result = (string?)null,
            })));

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var variables = await scope.ServiceProvider.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(_workflowId!);
        var settings = variables.Vars!.Value.GetProperty("settings");
        Assert.False(settings.TryGetProperty("old", out _));
        Assert.Equal(2, settings.GetProperty("keep").GetInt32());
        Assert.Equal(WorkflowRunStatus.Completed, (await LoadRunAsync(_workflowId!)).Status);
    }

    [Fact]
    public async Task WorkflowAgentAction_SetVarsNestedNullTarget_DeletesNestedProperty()
    {
        await ClearBacklogAsync();
        _workflowId = $"workflow-agent-set-vars-null-{Guid.NewGuid():N}";
        var projectId = TestProjectId(_workflowId);
        _runnerId = await RegisterRunnerAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId);
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
                    SetVars: new Dictionary<string, string>
                    {
                        ["settings"] = "output.result",
                    })
            ], [])
        ]);
        await SeedWorkflowTemplateAsync(_workflowId, definition, projectId);
        var repository = new WorkflowRepositoryContext(
            "web",
            "https://github.com/octocat/hello-world.git",
            "master");
        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(projectId, 1, null),
            new WorkflowStartSnapshot(repository, null));
        Assert.Equal(WorkflowAssignmentStatus.Assigned,
            (await workflow.AssignWorkerAsync(_runnerId)).Status);
        await workflow.PatchVariablesAsync(new VariableBundle(Vars: System.Text.Json.JsonSerializer.SerializeToElement(
            new { settings = new { old = 1, keep = 2 } })));

        Assert.Null(await workflow.ClaimNextAsync(_runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var dispatch = await PollWorkAsync(_runnerId);
        await ReportAsync(_runnerId, dispatch.Work, new WorkResult(
            "completed",
            Output: System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                result = new { old = (string?)null, keep = 3 },
            })));

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var variables = await scope.ServiceProvider.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(_workflowId!);
        var settings = variables.Vars!.Value.GetProperty("settings");
        Assert.False(settings.TryGetProperty("old", out _));
        Assert.Equal(3, settings.GetProperty("keep").GetInt32());
        Assert.Equal(WorkflowRunStatus.Completed, (await LoadRunAsync(_workflowId!)).Status);
    }

    [Fact]
    public async Task FeedbackAgentTerminal_QueuesPushExactlyOnceWithoutReopeningApproval()
    {
        var definition = new WorkflowDefinition(
        [
            new StageDefinition(
                "plan",
                [new TaskDefinition("draft", "Draft", "spec/task")],
                [new CheckDefinition("plan-ok", "Plan OK", "spec/check")],
                RequiresApproval: true),
        ],
        Approval: new ApprovalConfig(new ApprovalFeedbackConfig([
            AgentTask("apply-feedback", "Apply approval feedback", "feedback"),
            new TaskDefinition("publish-feedback", "Publish approval feedback", "mohist/push"),
        ])));
        var workflow = await StartWorkflowAsync(
            definition,
            $"workflow-agent-feedback-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;

        var (draft, _) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, draft, "completed");
        var (checks, _) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(runnerId, checks, "plan-ok");
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, (await LoadRunAsync(_workflowId!)).Status);

        var feedbackId = await workflow.RequestChangesAsync("apply and publish", "operator-1");
        var approvalRequestCount = (await EventStore.ListAsync(_workflowId!))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested);

        var (agent, _) = await PollWorkAnyAsync();
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, agent.OwnerKind);
        Assert.Equal("apply-feedback.1", agent.ActionAttemptId);
        await ReportAsync(runnerId, agent, "completed");

        var afterAgent = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Ready, afterAgent.Status);
        Assert.Equal(ApprovalFeedbackStatus.Open,
            afterAgent.Feedback.Single(item => item.Id == feedbackId).Status);
        var pushWork = Assert.IsType<WorkflowTaskWork>(afterAgent.NextWork());
        Assert.Equal("publish-feedback.1", pushWork.Id);
        Assert.Equal("mohist/push", pushWork.Uses);

        var terminalEnvelope = Assert.Single(EventStore.Appended,
            recorded => recorded.Envelope.Type == EventCatalog.ReverseDns.AgentJobWorkflowTerminal
                && string.Equals(recorded.Envelope.Subject, agent.AgentJobId, StringComparison.Ordinal));
        var eventCountBeforeReplay = EventStore.Appended.Count;
        var workflowEventCountBeforeReplay = (await EventStore.ListAsync(_workflowId!)).Count;
        await Services.GetRequiredService<AgentJobWorkflowTerminalHandler>()
            .HandleAsync(terminalEnvelope.Envelope, CancellationToken.None);

        Assert.Equal(eventCountBeforeReplay, EventStore.Appended.Count);
        Assert.Equal(workflowEventCountBeforeReplay, (await EventStore.ListAsync(_workflowId!)).Count);
        var afterReplay = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Ready, afterReplay.Status);
        Assert.Equal("publish-feedback.1", Assert.IsType<WorkflowTaskWork>(afterReplay.NextWork()).Id);

        var dispatch = Services.GetRequiredService<DispatchService>();
        var push = Assert.Single((await dispatch.PollAsync(
            runnerId,
            DispatchTestExtensions.ReadyPollRequest())).Dispatches);
        Assert.Equal(WorkDispatchOwnerKinds.Workflow, push.OwnerKind);
        Assert.Equal("publish-feedback.1", push.ActionAttemptId);
        Assert.Equal("mohist/push", push.Uses);
        Assert.Equal(approvalRequestCount, (await EventStore.ListAsync(_workflowId!))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested));

        var workflowEventsAfterClaim = (await EventStore.ListAsync(_workflowId!)).Count;
        var repeatedPoll = DispatchTestExtensions.ReadyPollRequest() with
        {
            InFlight = [$"{WorkDispatchOwnerKinds.Workflow}:{_workflowId}:{push.WorkId}"],
        };
        Assert.Empty((await dispatch.PollAsync(runnerId, repeatedPoll)).Dispatches);
        Assert.Equal(workflowEventsAfterClaim, (await EventStore.ListAsync(_workflowId!)).Count);

        await ReportAsync(runnerId, push, "completed");
        var resolved = await LoadRunAsync(_workflowId!);
        Assert.Equal(ApprovalFeedbackStatus.Resolved,
            resolved.Feedback.Single(item => item.Id == feedbackId).Status);
        Assert.Equal(2, resolved.CurrentStage().Attempt);
        Assert.Equal(approvalRequestCount, (await EventStore.ListAsync(_workflowId!))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested));
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
