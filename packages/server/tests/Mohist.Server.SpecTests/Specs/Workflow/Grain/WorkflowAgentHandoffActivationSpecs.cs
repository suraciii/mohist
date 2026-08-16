using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// Activation coverage for the durable handoff fence: an accepted receipt
/// materializes the reserved AgentJob, AgentSession, first SessionInput, and
/// first AgentTurn under the minted identifiers via a persisted step
/// machine, replays without duplication, resumes after acknowledgement loss
/// or crash, and never re-reads mutable Agent configuration. Prepared and
/// rejected plans never materialize anything.
/// </summary>
[Collection("WorkflowAgentHandoff")]
public sealed class WorkflowAgentHandoffActivationSpecs
{
    private readonly WorkflowAgentHandoffGrainFixture _fixture;

    public WorkflowAgentHandoffActivationSpecs(WorkflowAgentHandoffGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Preflight.Reset();
        _fixture.ActivationFaults.ClearObservations();
        foreach (var gate in Enum.GetValues<WorkflowAgentHandoffGate>())
            _fixture.ActivationFaults.StopFailing(gate);
    }

    [Fact]
    public async Task PrepareAsync_FreezesAgentIdentityExpectSessionNameAndWorkspaceNextToDefinition()
    {
        var projectId = $"handoff-freeze-{Guid.NewGuid():N}";
        var agentRef = $"agent_freeze_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var definition = Definition(AgentConfigSchema.PiRuntime, skills: ["review-skill"]);
        _fixture.Preflight.Set(projectId, agentRef, definition, agentId, "Freeze Agent");
        var command = Command(projectId, agentRef, "freeze every rendered fact", session: "named-session");
        _fixture.Preflight.SetRunContext(command.WorkflowRunId, new WorkflowAgentHandoffRunContext(
            IssueNumber: 42,
            EpicNumber: 7,
            Workspace: new WorkflowAgentHandoffWorkspace(Name: "issue-42", Path: null, Branch: null)));
        var handoff = Handoff(command);

        var prepared = await handoff.PrepareAsync(command);
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Prepared, prepared.Disposition);
        Assert.NotNull(plan);
        Assert.Equal(agentId, plan!.AgentId);
        Assert.Equal("Freeze Agent", plan.AgentName);
        Assert.Equal("named-session", plan.SessionName);
        Assert.Equal(command.Expect, plan.Command.Expect);
        Assert.NotNull(plan.RunContext);
        Assert.Equal(42, plan.RunContext!.IssueNumber);
        Assert.Equal(7, plan.RunContext.EpicNumber);
        Assert.Equal("issue-42", plan.RunContext.Workspace!.Name);
        Assert.Equal(definition.Instructions, plan.ExecutionDefinition!.Instructions);
        Assert.Equal(definition.Runtime, plan.ExecutionDefinition.Runtime);
        Assert.Equal(definition.Model, plan.ExecutionDefinition.Model);
        Assert.Equal(definition.Variant, plan.ExecutionDefinition.Variant);
        Assert.Equal(definition.Skills, plan.ExecutionDefinition.Skills);
        // No workspace on the run and no explicit session: the logical
        // session name defaults to the work id (== command id).
        var bare = Command(projectId, agentRef, "unbound defaults", session: null);
        _fixture.Preflight.SetRunContext(bare.WorkflowRunId, new WorkflowAgentHandoffRunContext(null, null, null));
        var bareHandoff = Handoff(bare);
        await bareHandoff.PrepareAsync(bare);
        var barePlan = await bareHandoff.GetPlanAsync();
        Assert.NotNull(barePlan);
        Assert.Equal(bare.CommandId, barePlan!.SessionName);
        Assert.Null(barePlan.RunContext!.Workspace);
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task PrepareAsync_ConflictingExpectRender_ConflictsWithoutMutatingTheStoredPlan()
    {
        var projectId = $"handoff-expect-conflict-{Guid.NewGuid():N}";
        var agentRef = $"agent_expect_{Guid.NewGuid():N}";
        _fixture.Preflight.Set(projectId, agentRef, Definition(AgentConfigSchema.PiRuntime));
        var first = Command(projectId, agentRef, "freeze the task contract");
        var handoff = Handoff(first);
        var prepared = await handoff.PrepareAsync(first);

        var conflict = first with { Expect = "{\"files\":[\"other.md\"]}" };
        var error = await Assert.ThrowsAsync<WorkflowAgentHandoffConflictException>(
            () => handoff.PrepareAsync(conflict));
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(first.CommandId, error.CommandId);
        Assert.Equal(WorkflowAgentHandoffCodec.Fingerprint(first), error.ExistingFingerprint);
        Assert.NotNull(plan);
        Assert.Equal(first.Expect, plan!.Command.Expect);
        Assert.Equal(prepared.Invocation, plan.Invocation);
        Assert.Equal(1, _fixture.Preflight.ResolveCount(projectId, agentRef));
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task ActivateAsync_OnAcceptedReceipt_MaterializesParticipantsUnderMintedIdentifiers()
    {
        var projectId = $"handoff-activate-{Guid.NewGuid():N}";
        var agentRef = $"agent_activate_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var definition = Definition(AgentConfigSchema.PiRuntime, skills: ["review-skill"]);
        _fixture.Preflight.Set(projectId, agentRef, definition, agentId, "Activate Agent");
        var command = Command(projectId, agentRef, "materialize the reserved lineage");
        _fixture.Preflight.SetRunContext(command.WorkflowRunId, new WorkflowAgentHandoffRunContext(
            IssueNumber: 128,
            EpicNumber: null,
            Workspace: new WorkflowAgentHandoffWorkspace(Name: "issue-128", Path: null, Branch: null)));
        var handoff = Handoff(command);
        await handoff.PrepareAsync(command);
        await handoff.AcceptAsync(Acceptance(command));
        var invocation = (await handoff.GetPlanAsync())!.Invocation!;

        var activated = await handoff.ActivateAsync();
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Activated, activated.Disposition);
        Assert.False(activated.AlreadyActivated);
        Assert.Equal(invocation, activated.Invocation);
        Assert.NotNull(plan);
        Assert.Equal(WorkflowAgentHandoffDisposition.Activated, plan!.Disposition);
        Assert.NotNull(plan.ActivatedAt);

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            var ledger = await jobs.LoadLedgerAsync(invocation.JobKey);
            Assert.NotNull(ledger);
            Assert.Equal("visible", ledger!.LaunchVisibility, StringComparer.Ordinal);
            var state = JSON.Deserialize<AgentJobState>(ledger.StateJson)!;
            Assert.NotNull(state.Input);
            Assert.Equal(command.Prompt, state.Input!.Prompt);
            Assert.Equal(agentId, state.Input.AgentId);
            Assert.Equal(definition.Runtime, state.Input.Runtime);
            Assert.Equal(definition.Model, state.Input.Model);
            Assert.Equal(definition.Variant, state.Input.Variant);
            Assert.Equal(definition.Instructions, state.Input.AgentInstructions);
            Assert.Equal(definition.Skills, state.Input.Skills);
            Assert.Equal(invocation.SessionId, state.Input.AgentSessionId);
            Assert.Equal(invocation.InputId, state.Input.InitialInputId);
            Assert.Equal(invocation.TurnId, state.Input.InitialTurnId);
            Assert.Equal(command.WorkflowRunId, state.Input.WorkflowRunId);
            Assert.Equal(
                new AgentJobWorkflowInvocation(invocation.InvocationId, command.TaskRunId, command.CommandId),
                state.Input.WorkflowInvocation);

            var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
            var session = await sessions.LoadAsync(invocation.SessionId);
            Assert.NotNull(session);
            var input = Assert.Single(session!.Status.Inputs ?? []);
            Assert.Equal(invocation.InputId, input.Id);
            Assert.Equal(command.Prompt, input.Text);
            Assert.Equal("workflow", input.Source);
            Assert.Equal(invocation.JobKey, input.JobId);
            var turn = Assert.Single(session.Status.Turns ?? []);
            Assert.Equal(invocation.TurnId, turn.Id);
            Assert.Equal(invocation.InputId, Assert.Single(turn.InputIds));
            Assert.Equal(invocation.JobKey, turn.JobId);
            Assert.Equal(AgentTurnStatus.Queued, turn.Status);
            Assert.Equal("workflow", session.Metadata.Label(AgentSessionQueryMetadataKeys.SourceKind));
            Assert.Equal(projectId, session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId));
            Assert.Equal(command.WorkflowRunId, session.Metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId));
            Assert.Equal(command.CommandId, session.Metadata.Label(AgentSessionQueryMetadataKeys.SessionName));
            Assert.Equal(command.CommandId, session.Metadata.Label(AgentSessionQueryMetadataKeys.WorkId));
            Assert.Equal(command.TaskRunId, session.Metadata.Label(AgentSessionQueryMetadataKeys.TaskRunId));
            Assert.Equal(invocation.InvocationId, session.Metadata.Label(AgentSessionQueryMetadataKeys.InvocationId));
            Assert.Equal(agentId, session.Metadata.Label(GenericAgentSessionMetadata.AgentId));
            Assert.Equal("Activate Agent", session.Metadata.Label(GenericAgentSessionMetadata.AgentName));
            Assert.Equal("issue-128", session.Metadata.Label(GenericAgentSessionMetadata.WorkspaceName));
        }
    }

    [Fact]
    public async Task ActivateAsync_FreezesTheDeclaredOrDefaultDeadlineAndExpectOntoTheJobInput()
    {
        var projectId = $"handoff-deadline-{Guid.NewGuid():N}";
        var agentRef = $"agent_deadline_{Guid.NewGuid():N}";
        _fixture.Preflight.Set(projectId, agentRef, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentRef, "declare the execution deadline");
        var handoff = Handoff(command);
        await handoff.PrepareAsync(command);
        await handoff.AcceptAsync(Acceptance(command));

        var activated = await handoff.ActivateAsync();
        var invocation = activated.Invocation!;

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            var state = JSON.Deserialize<AgentJobState>(
                (await jobs.LoadLedgerAsync(invocation.JobKey))!.StateJson)!;
            // D4: the frozen TimeoutMilliseconds becomes the per-invocation
            // deadline on AgentJobInput; the frozen expect rides the input
            // so dispatch can project it onto WorkDispatch.Expect.
            Assert.Equal(command.TimeoutMilliseconds, state.Input!.TimeoutMilliseconds);
            Assert.Equal(command.Expect, state.Input.Expect);
        }

        // An omitted task timeout resolves to the runtime action default
        // (60 minutes) at the activation boundary, matching inline
        // mohist/opencode / mohist/pi semantics instead of the shorter
        // global AgentJobOptions.JobTimeout backstop.
        var omitted = Command(projectId, agentRef, "default the execution deadline") with
        {
            TimeoutMilliseconds = null,
        };
        var omittedHandoff = Handoff(omitted);
        await omittedHandoff.PrepareAsync(omitted);
        await omittedHandoff.AcceptAsync(Acceptance(omitted));

        var omittedActivated = await omittedHandoff.ActivateAsync();

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            var state = JSON.Deserialize<AgentJobState>(
                (await jobs.LoadLedgerAsync(omittedActivated.Invocation!.JobKey))!.StateJson)!;
            Assert.Equal(WorkflowAgentHandoffDeadline.DefaultTimeoutMilliseconds, state.Input!.TimeoutMilliseconds);
            Assert.Equal(3_600_000, WorkflowAgentHandoffDeadline.DefaultTimeoutMilliseconds);
            Assert.Equal(omitted.Expect, state.Input.Expect);
        }
    }

    [Fact]
    public async Task ActivateAsync_ReplayOfActivatedPlan_IsNoOpReturningTheSameInvocation()
    {
        var projectId = $"handoff-replay-activated-{Guid.NewGuid():N}";
        var agentRef = $"agent_replay_activated_{Guid.NewGuid():N}";
        _fixture.Preflight.Set(projectId, agentRef, Definition(AgentConfigSchema.OpenCodeRuntime));
        var command = Command(projectId, agentRef, "activate exactly once");
        var handoff = Handoff(command);
        await handoff.PrepareAsync(command);
        await handoff.AcceptAsync(Acceptance(command));
        var first = await handoff.ActivateAsync();
        var invocation = first.Invocation!;
        _fixture.ActivationFaults.ClearObservations();

        var replay = await handoff.ActivateAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Activated, replay.Disposition);
        Assert.True(replay.AlreadyActivated);
        Assert.Equal(invocation, replay.Invocation);
        Assert.Equal(first.Invocation, (await handoff.GetPlanAsync())!.Invocation);
        // No participant was touched again.
        Assert.Empty(_fixture.ActivationFaults.ParticipantIds(WorkflowAgentHandoffGate.PrepareJob));
        Assert.Empty(_fixture.ActivationFaults.ParticipantIds(WorkflowAgentHandoffGate.EnsureInitialLaunch));
        Assert.Empty(_fixture.ActivationFaults.ParticipantIds(WorkflowAgentHandoffGate.SubmitJob));
        await AssertSingleLineageAsync(projectId, invocation);
    }

    [Theory]
    [InlineData(WorkflowAgentHandoffGate.PrepareJob)]
    [InlineData(WorkflowAgentHandoffGate.EnsureInitialLaunch)]
    [InlineData(WorkflowAgentHandoffGate.SubmitJob)]
    public async Task ActivateAsync_AcknowledgementLossAfterParticipantWrite_ResumesWithExactIdentifiers(
        WorkflowAgentHandoffGate gate)
    {
        var projectId = $"handoff-loss-{gate}-{Guid.NewGuid():N}";
        var agentRef = $"agent_loss_{gate}_{Guid.NewGuid():N}";
        _fixture.Preflight.Set(projectId, agentRef, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentRef, "resume after acknowledgement loss");
        var handoff = Handoff(command);
        await handoff.PrepareAsync(command);
        await handoff.AcceptAsync(Acceptance(command));

        try
        {
            _fixture.ActivationFaults.FailNext(gate);
            await Assert.ThrowsAsync<WorkflowAgentHandoffActivationPendingException>(
                () => handoff.ActivateAsync());

            // The step whose acknowledgement was lost has durably written its
            // participant facts; the cursor is still on that step.
            var plan = await handoff.GetPlanAsync();
            Assert.NotNull(plan);
            Assert.Equal(WorkflowAgentHandoffDisposition.Accepted, plan!.Disposition);
            var jobKey = Assert.Single(_fixture.ActivationFaults.ParticipantIds(WorkflowAgentHandoffGate.PrepareJob));
            if (gate != WorkflowAgentHandoffGate.PrepareJob)
            {
                Assert.Single(_fixture.ActivationFaults.ParticipantIds(WorkflowAgentHandoffGate.EnsureInitialLaunch));
            }
            if (gate == WorkflowAgentHandoffGate.SubmitJob)
            {
                Assert.Single(_fixture.ActivationFaults.ParticipantIds(WorkflowAgentHandoffGate.SubmitJob));
            }

            // Simulate a crash: drop the activation and resume from the
            // persisted cursor on the next command.
            _fixture.ActivationFaults.StopFailing(gate);
            await handoff.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
            var resumed = await handoff.ActivateAsync();

            Assert.Equal(WorkflowAgentHandoffDisposition.Activated, resumed.Disposition);
            Assert.Equal(plan.Invocation, resumed.Invocation);
            var invocation = resumed.Invocation!;
            Assert.Equal(jobKey, invocation.JobKey);
            await AssertSingleLineageAsync(projectId, invocation);
            // Every step ran under the same job and session participants —
            // no duplicates, no replacement work.
            Assert.Equal(
                [invocation.JobKey],
                _fixture.ActivationFaults.ParticipantIds(WorkflowAgentHandoffGate.PrepareJob)
                    .Where(id => string.Equals(id, invocation.JobKey, StringComparison.Ordinal))
                    .Distinct()
                    .ToArray());
        }
        finally
        {
            _fixture.ActivationFaults.StopFailing(gate);
        }
    }

    [Fact]
    public async Task ActivateAsync_AfterAgentEdit_UsesTheFrozenDefinitionWithoutRereadingConfiguration()
    {
        var projectId = $"handoff-frozen-{Guid.NewGuid():N}";
        var agentRef = $"agent_frozen_{Guid.NewGuid():N}";
        var frozen = Definition(AgentConfigSchema.PiRuntime, instructions: "frozen instructions");
        _fixture.Preflight.Set(projectId, agentRef, frozen);
        var command = Command(projectId, agentRef, "run the accepted definition");
        var handoff = Handoff(command);
        await handoff.PrepareAsync(command);
        await handoff.AcceptAsync(Acceptance(command));

        // The Agent definition is edited after acceptance.
        _fixture.Preflight.Set(
            projectId,
            agentRef,
            Definition(AgentConfigSchema.OpenCodeRuntime, instructions: "edited instructions"));

        var activated = await handoff.ActivateAsync();
        var invocation = activated.Invocation!;

        Assert.Equal(WorkflowAgentHandoffDisposition.Activated, activated.Disposition);
        Assert.Equal(1, _fixture.Preflight.ResolveCount(projectId, agentRef));
        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            var state = JSON.Deserialize<AgentJobState>(
                (await jobs.LoadLedgerAsync(invocation.JobKey))!.StateJson)!;
            Assert.Equal(frozen.Runtime, state.Input!.Runtime);
            Assert.Equal(frozen.Instructions, state.Input.AgentInstructions);

            var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
            var session = await sessions.LoadAsync(invocation.SessionId);
            Assert.NotNull(session);
        }
    }

    [Fact]
    public async Task ActivateAsync_OnPreparedPlan_IsRefusedAndNeverMaterializes()
    {
        var projectId = $"handoff-prepared-{Guid.NewGuid():N}";
        var agentRef = $"agent_prepared_{Guid.NewGuid():N}";
        _fixture.Preflight.Set(projectId, agentRef, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentRef, "never materialize a prepared plan");
        var handoff = Handoff(command);
        var prepared = await handoff.PrepareAsync(command);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handoff.ActivateAsync());

        var plan = await handoff.GetPlanAsync();
        Assert.NotNull(plan);
        Assert.Equal(WorkflowAgentHandoffDisposition.Prepared, plan!.Disposition);
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task ActivateAsync_OnRejectedPlan_ReplaysTheFrozenRejectionWithoutMaterializing()
    {
        var projectId = $"handoff-rejected-{Guid.NewGuid():N}";
        var agentRef = $"agent_rejected_{Guid.NewGuid():N}";
        var command = Command(projectId, agentRef, "persist the definitive rejection");
        var handoff = Handoff(command);
        var rejected = await handoff.PrepareAsync(command);

        // The Agent now exists; the frozen rejection must still replay.
        _fixture.Preflight.Set(projectId, agentRef, Definition(AgentConfigSchema.PiRuntime));
        var error = await Assert.ThrowsAsync<WorkflowAgentHandoffRejectedException>(
            () => handoff.ActivateAsync());
        var replay = await Assert.ThrowsAsync<WorkflowAgentHandoffRejectedException>(
            () => handoff.ActivateAsync());

        Assert.Equal("agent_not_found", error.Rejection.Code);
        Assert.Equal(rejected.Rejection, error.Rejection);
        Assert.Equal(rejected.Rejection, replay.Rejection);
        var plan = await handoff.GetPlanAsync();
        Assert.NotNull(plan);
        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, plan!.Disposition);
        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            Assert.Empty(await jobs.ListEligiblePendingAsync(projectId, 10));
        }
    }

    private IWorkflowAgentHandoffGrain Handoff(WorkflowAgentHandoffCommand command) =>
        _fixture.Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(
                command.ProjectId,
                command.WorkflowRunId,
                command.TaskRunId,
                command.CommandId));

    private static WorkflowAgentHandoffAcceptance Acceptance(WorkflowAgentHandoffCommand command) =>
        new(command.CommandId, WorkflowAgentHandoffCodec.Fingerprint(command));

    private static WorkflowAgentHandoffCommand Command(
        string projectId,
        string agentRef,
        string prompt,
        string? session = null) =>
        new(
            CommandId: $"workflow-work-{Guid.NewGuid():N}",
            ProjectId: projectId,
            WorkflowRunId: $"workflow-run-{Guid.NewGuid():N}",
            TaskRunId: $"task-run-{Guid.NewGuid():N}",
            AgentRef: agentRef,
            Prompt: prompt,
            Session: session,
            TimeoutMilliseconds: 60_000,
            Expect: "{\"files\":[\"plans/agent.md\"]}");

    private static AgentExecutionDefinition Definition(
        string runtime,
        string? instructions = null,
        string[]? skills = null) =>
        new(
            Instructions: instructions ?? "follow the workflow task",
            Runtime: runtime,
            Model: "model-test",
            Variant: "high",
            Skills: skills ?? [],
            AllowedSubagents: null);

    private async Task AssertSingleLineageAsync(string projectId, WorkflowAgentInvocation invocation)
    {
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var sessionsStore = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();

        var ledger = await jobs.LoadLedgerAsync(invocation.JobKey);
        Assert.NotNull(ledger);
        var state = JSON.Deserialize<AgentJobState>(ledger!.StateJson)!;
        Assert.Equal(invocation.SessionId, state.Input!.AgentSessionId);
        Assert.Equal(invocation.InputId, state.Input.InitialInputId);
        Assert.Equal(invocation.TurnId, state.Input.InitialTurnId);

        var session = await sessionsStore.LoadAsync(invocation.SessionId);
        Assert.NotNull(session);
        Assert.Equal(invocation.InputId, Assert.Single(session!.Status.Inputs ?? []).Id);
        Assert.Equal(invocation.TurnId, Assert.Single(session.Status.Turns ?? []).Id);
    }

    private async Task AssertNoParticipantsAsync(string projectId, WorkflowAgentInvocation invocation)
    {
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();

        Assert.Null(await jobs.LoadLedgerAsync(invocation.JobKey));
        Assert.Empty(await jobs.ListEligiblePendingAsync(projectId, 10));
        Assert.Null(await sessions.LoadAsync(invocation.SessionId));
    }
}
