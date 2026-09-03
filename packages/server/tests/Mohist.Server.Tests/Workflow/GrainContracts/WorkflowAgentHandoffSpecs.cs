using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Workflow.GrainContracts;

/// <summary>
/// The Workflow Agent handoff fence without a cluster: durable preflight
/// freezing, fingerprint conflict rejection, acceptance receipts, and the
/// guarantee that prepared or accepted handoffs never materialize an
/// AgentJob or AgentSession. Activation loss is replayed by constructing a
/// fresh grain over the same persistent storage (#681).
/// </summary>
[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowAgentHandoffSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;
    private readonly WorkflowAgentHandoffPreflightProbe _preflight = new();

    public WorkflowAgentHandoffSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PrepareAsync_ReplayAfterActivationLoss_ReusesFrozenGenericInvocationWithoutParticipants()
    {
        var projectId = $"workflow-handoff-replay-{Guid.NewGuid():N}";
        var agentId = $"agent_replay_{Guid.NewGuid():N}";
        var canonicalAgentId = $"canonical-agent-{Guid.NewGuid():N}";
        var store = new ConcurrentDictionary<string, WorkflowAgentHandoffState>();
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime), canonicalAgentId);
        var command = Command(projectId, agentId, "keep the original definition");

        var handoff = await ActivateAsync(Key(command), store);
        var prepared = await handoff.PrepareAsync(command);

        Assert.Equal(WorkflowAgentHandoffDisposition.Prepared, prepared.Disposition);
        Assert.NotNull(prepared.Invocation);
        Assert.False(prepared.AlreadyPersisted);
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);

        // Activation loss: a fresh grain instance over the same storage.
        handoff = await ActivateAsync(Key(command), store);
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.OpenCodeRuntime));

        var replay = await handoff.PrepareAsync(command);
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Prepared, replay.Disposition);
        Assert.True(replay.AlreadyPersisted);
        Assert.Equal(prepared.Invocation, replay.Invocation);
        Assert.NotNull(plan);
        Assert.Equal(AgentConfigSchema.PiRuntime, plan!.ExecutionDefinition!.Runtime);
        Assert.Equal(canonicalAgentId, plan.AgentId);
        AssertCompletionSnapshot(command.Completion!, plan.Command.Completion!);
        Assert.Equal(1, _preflight.ResolveCount(projectId, agentId));
        await AssertNoParticipantsAsync(projectId, prepared.Invocation);
    }

    [Fact]
    public async Task PrepareAsync_ConflictingRenderedInput_PreservesFirstInvocation()
    {
        var projectId = $"workflow-handoff-conflict-{Guid.NewGuid():N}";
        var agentId = $"agent_conflict_{Guid.NewGuid():N}";
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var first = Command(projectId, agentId, "first rendered prompt");
        var handoff = await ActivateAsync(Key(first), new());

        var prepared = await handoff.PrepareAsync(first);
        var conflict = first with { Prompt = "different rendered prompt" };

        var error = await Assert.ThrowsAsync<WorkflowAgentHandoffConflictException>(
            () => handoff.PrepareAsync(conflict));
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(first.CommandId, error.CommandId);
        Assert.Equal(WorkflowAgentHandoffCodec.Fingerprint(first), error.ExistingFingerprint);
        Assert.NotNull(plan);
        Assert.Equal(first.Prompt, plan!.Command.Prompt);
        Assert.Equal(prepared.Invocation, plan.Invocation);
        Assert.Equal(1, _preflight.ResolveCount(projectId, agentId));
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task PrepareAsync_CompletionSnapshotCanonicalizesMapsAndRejectsChangedEffects()
    {
        var projectId = $"workflow-handoff-completion-{Guid.NewGuid():N}";
        var agentId = $"agent_completion_{Guid.NewGuid():N}";
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "freeze completion effects");
        var handoff = await ActivateAsync(Key(command), new());

        var prepared = await handoff.PrepareAsync(command);
        var reordered = command with
        {
            Completion = command.Completion! with
            {
                ExpectJson = "{\"files\":[{\"path\":\"review.md\",\"markers\":[\"approved\"]}],\"markers\":[{\"path\":\"review.md\",\"contains\":\"approved\"}]}",
            },
        };
        var replay = await handoff.PrepareAsync(reordered);
        var changed = command with
        {
            Completion = command.Completion! with
            {
                SetVars = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["result"] = "changed",
                },
            },
        };

        var error = await Assert.ThrowsAsync<WorkflowAgentHandoffConflictException>(
            () => handoff.PrepareAsync(changed));
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(
            WorkflowAgentHandoffCodec.Fingerprint(command),
            WorkflowAgentHandoffCodec.Fingerprint(reordered));
        Assert.True(replay.AlreadyPersisted);
        Assert.Equal(prepared.Invocation, replay.Invocation);
        Assert.Equal(WorkflowAgentHandoffCodec.Fingerprint(command), error.ExistingFingerprint);
        Assert.NotNull(plan);
        AssertCompletionSnapshot(command.Completion!, plan!.Command.Completion!);
        Assert.Equal(1, _preflight.ResolveCount(projectId, agentId));
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task PrepareAsync_IncompleteCompletionSnapshot_PersistsRejectionBeforePreflight()
    {
        var projectId = $"workflow-handoff-invalid-completion-{Guid.NewGuid():N}";
        var agentId = $"agent_invalid_completion_{Guid.NewGuid():N}";
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "do not resolve incomplete completion") with
        {
            Completion = Command(projectId, agentId, "unused").Completion! with { Stage = "" },
        };
        var handoff = await ActivateAsync(Key(command), new());

        var rejected = await handoff.PrepareAsync(command);
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, rejected.Disposition);
        Assert.Equal("invalid_completion_snapshot", rejected.Rejection!.Code);
        Assert.Null(rejected.Invocation);
        Assert.NotNull(plan);
        Assert.Null(plan!.Invocation);
        Assert.Null(plan.ExecutionDefinition);
        Assert.Null(plan.AgentId);
        Assert.Equal(0, _preflight.ResolveCount(projectId, agentId));
        Assert.Empty(await ListEligibleAgentJobsAsync(projectId));
    }

    [Fact]
    public async Task PrepareAsync_MissingAgent_PersistsPreflightRejection()
    {
        var projectId = $"workflow-handoff-rejection-{Guid.NewGuid():N}";
        var agentId = $"agent_missing_{Guid.NewGuid():N}";
        var command = Command(projectId, agentId, "must not start");
        var store = new ConcurrentDictionary<string, WorkflowAgentHandoffState>();
        var handoff = await ActivateAsync(Key(command), store);

        var rejected = await handoff.PrepareAsync(command);
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        handoff = await ActivateAsync(Key(command), store);
        var replay = await handoff.PrepareAsync(command);
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, rejected.Disposition);
        Assert.Equal("agent_not_found", rejected.Rejection!.Code);
        Assert.Null(rejected.Invocation);
        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, replay.Disposition);
        Assert.True(replay.AlreadyPersisted);
        Assert.Equal(rejected.Rejection, replay.Rejection);
        Assert.NotNull(plan);
        Assert.Null(plan!.Invocation);
        Assert.Null(plan.ExecutionDefinition);
        Assert.Equal(1, _preflight.ResolveCount(projectId, agentId));
        Assert.Empty(await ListEligibleAgentJobsAsync(projectId));
    }

    [Fact]
    public async Task PrepareAsync_RejectionReplayAfterActivationLoss_DoesNotRerunPreflight()
    {
        var projectId = $"workflow-handoff-rejection-replay-{Guid.NewGuid():N}";
        var agentId = $"agent_missing_replay_{Guid.NewGuid():N}";
        var command = Command(projectId, agentId, "persist the definitive rejection");
        var store = new ConcurrentDictionary<string, WorkflowAgentHandoffState>();
        var handoff = await ActivateAsync(Key(command), store);

        var rejected = await handoff.PrepareAsync(command);
        handoff = await ActivateAsync(Key(command), store);
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));

        var replay = await handoff.PrepareAsync(command);
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, rejected.Disposition);
        Assert.Equal("agent_not_found", rejected.Rejection!.Code);
        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, replay.Disposition);
        Assert.True(replay.AlreadyPersisted);
        Assert.Equal(rejected.Rejection, replay.Rejection);
        Assert.Equal(1, _preflight.ResolveCount(projectId, agentId));
        Assert.NotNull(plan);
        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, plan!.Disposition);
        Assert.Null(plan.Invocation);
        Assert.Empty(await ListEligibleAgentJobsAsync(projectId));
    }

    [Fact]
    public async Task AcceptAsync_WritesOnlyReceipt_AndLeavesJobAndSessionUnmaterialized()
    {
        var projectId = $"workflow-handoff-accept-{Guid.NewGuid():N}";
        var agentId = $"agent_accept_{Guid.NewGuid():N}";
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "await workflow acceptance");
        var handoff = await ActivateAsync(Key(command), new());
        var prepared = await handoff.PrepareAsync(command);

        var accepted = await handoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            command.CommandId,
            WorkflowAgentHandoffCodec.Fingerprint(command)));
        var replay = await handoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            command.CommandId,
            WorkflowAgentHandoffCodec.Fingerprint(command)));
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Accepted, accepted.Disposition);
        Assert.False(accepted.AlreadyPersisted);
        Assert.Equal(prepared.Invocation, accepted.Invocation);
        Assert.Equal(WorkflowAgentHandoffDisposition.Accepted, replay.Disposition);
        Assert.True(replay.AlreadyPersisted);
        Assert.NotNull(plan!.AcceptedAt);
        AssertCompletionSnapshot(command.Completion!, plan.Command.Completion!);
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task AcceptAsync_ReplayAfterActivationLoss_ReusesThePersistedReceipt()
    {
        var projectId = $"workflow-handoff-accept-replay-{Guid.NewGuid():N}";
        var agentId = $"agent_accept_replay_{Guid.NewGuid():N}";
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "replay the acceptance receipt");
        var store = new ConcurrentDictionary<string, WorkflowAgentHandoffState>();
        var handoff = await ActivateAsync(Key(command), store);
        var prepared = await handoff.PrepareAsync(command);
        var acceptance = new WorkflowAgentHandoffAcceptance(
            command.CommandId,
            WorkflowAgentHandoffCodec.Fingerprint(command));

        var accepted = await handoff.AcceptAsync(acceptance);
        var acceptedPlan = await handoff.GetPlanAsync();
        handoff = await ActivateAsync(Key(command), store);

        var replay = await handoff.AcceptAsync(acceptance);
        var replayPlan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Prepared, prepared.Disposition);
        Assert.Equal(WorkflowAgentHandoffDisposition.Accepted, accepted.Disposition);
        Assert.False(accepted.AlreadyPersisted);
        Assert.Equal(WorkflowAgentHandoffDisposition.Accepted, replay.Disposition);
        Assert.True(replay.AlreadyPersisted);
        Assert.Equal(accepted.Invocation, replay.Invocation);
        Assert.Equal(acceptedPlan!.AcceptedAt, replayPlan!.AcceptedAt);
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task AcceptAsync_ConflictingFingerprint_PreservesTheAcceptedReceipt()
    {
        var projectId = $"workflow-handoff-accept-conflict-{Guid.NewGuid():N}";
        var agentId = $"agent_accept_conflict_{Guid.NewGuid():N}";
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "accept the original prompt");
        var handoff = await ActivateAsync(Key(command), new());
        var prepared = await handoff.PrepareAsync(command);
        var accepted = await handoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            command.CommandId,
            WorkflowAgentHandoffCodec.Fingerprint(command)));
        var conflicting = command with { Prompt = "accept a different prompt" };

        var error = await Assert.ThrowsAsync<WorkflowAgentHandoffConflictException>(() =>
            handoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
                command.CommandId,
                WorkflowAgentHandoffCodec.Fingerprint(conflicting))));
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(command.CommandId, error.CommandId);
        Assert.Equal(WorkflowAgentHandoffCodec.Fingerprint(command), error.ExistingFingerprint);
        Assert.Equal(WorkflowAgentHandoffDisposition.Accepted, accepted.Disposition);
        Assert.NotNull(plan);
        Assert.Equal(WorkflowAgentHandoffDisposition.Accepted, plan!.Disposition);
        Assert.Equal(prepared.Invocation, plan.Invocation);
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task PrepareAsync_MismatchedGrainKey_RejectsBeforePreflight()
    {
        var projectId = $"workflow-handoff-key-{Guid.NewGuid():N}";
        var agentId = $"agent_key_{Guid.NewGuid():N}";
        _preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "do not persist under another command");
        var wrongKey = WorkflowAgentHandoffCodec.KeyFor(
            projectId,
            command.WorkflowRunId,
            command.ActionAttemptId,
            $"other-{command.CommandId}");
        var handoff = await ActivateAsync(wrongKey, new());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handoff.PrepareAsync(command));

        Assert.Contains("grain key does not match", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _preflight.ResolveCount(projectId, agentId));
    }

    [Fact]
    public void InvocationFor_NamedSession_ParticipatesInSessionIdentity()
    {
        var command = Command("project-1", "mohist/builder", "build");
        var first = WorkflowAgentHandoffCodec.InvocationFor(command with { Session = "delivery" });
        var second = WorkflowAgentHandoffCodec.InvocationFor(command with { Session = "review" });

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(first.JobKey, second.JobKey);
    }

    /// <summary>
    /// Constructs an activated handoff grain over <paramref name="store"/>.
    /// The persisted state is read before OnActivateAsync, mirroring how the
    /// runtime hydrates [PersistentState] before grain activation.
    /// </summary>
    private async Task<WorkflowAgentHandoffGrain> ActivateAsync(
        string key,
        ConcurrentDictionary<string, WorkflowAgentHandoffState> store)
    {
        var state = new TestGrainStorage<WorkflowAgentHandoffState>(key, store);
        await state.ReadStateAsync();
        var grain = new WorkflowAgentHandoffGrain(state, _preflight, TimeProvider);
        WorkflowGrainContractSupport.AttachTestContext(grain, key);
        await grain.OnActivateAsync(CancellationToken.None);
        return grain;
    }

    private static string Key(WorkflowAgentHandoffCommand command) =>
        WorkflowAgentHandoffCodec.KeyFor(
            command.ProjectId,
            command.WorkflowRunId,
            command.ActionAttemptId,
            command.CommandId);

    private static WorkflowAgentHandoffCommand Command(
        string projectId,
        string agentId,
        string prompt)
    {
        var commandId = $"workflow-work-{Guid.NewGuid():N}";
        return new(
            CommandId: commandId,
            ProjectId: projectId,
            WorkflowRunId: $"workflow-run-{Guid.NewGuid():N}",
            ActionAttemptId: $"task-run-{Guid.NewGuid():N}",
            AgentRef: agentId,
            Prompt: prompt,
            Session: "workflow-session",
            TimeoutMilliseconds: 60_000,
            Completion: Completion(commandId));
    }

    private static WorkflowAgentHandoffCompletionSnapshot Completion(string commandId) =>
        new(
            WorkId: commandId,
            Stage: "build",
            Workspace: new WorkflowAgentHandoffWorkspace(
                Name: "issue-559",
                Identity: null),
            ExpectJson: "{\"markers\":[{\"path\":\"review.md\",\"contains\":\"approved\"}],\"files\":[{\"path\":\"review.md\",\"markers\":[\"approved\"]}]}",
            Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]),
            SetVars: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["result"] = "${{ tasks.build.output }}",
            },
            Recovery: new RecoveryDefinition(Budget: 2, Handlers: []),
            RecoveryRemaining: 2);

    private static AgentExecutionDefinition Definition(string runtime) =>
        new(
            Instructions: "follow the workflow task",
            Runtime: runtime,
            Model: "model-test",
            Variant: "high",
            Skills: []);

    private static void AssertCompletionSnapshot(
        WorkflowAgentHandoffCompletionSnapshot expected,
        WorkflowAgentHandoffCompletionSnapshot actual)
    {
        Assert.Equal(expected.WorkId, actual.WorkId);
        Assert.Equal(expected.Stage, actual.Stage);
        Assert.Equal(expected.Workspace, actual.Workspace);
        Assert.Equal(expected.ExpectJson, actual.ExpectJson);
        Assert.Equal(expected.Artifacts!.Files.Select(file => file.Path), actual.Artifacts!.Files.Select(file => file.Path));
        Assert.Equal(expected.SetVars!["result"], actual.SetVars!["result"]);
        Assert.Equal(expected.Recovery!.Budget, actual.Recovery!.Budget);
        Assert.Equal(expected.RecoveryRemaining, actual.RecoveryRemaining);
    }

    private async Task AssertNoParticipantsAsync(string projectId, WorkflowAgentInvocation invocation)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();

        Assert.Null(await jobs.LoadLedgerAsync(invocation.JobKey));
        Assert.Empty(await jobs.ListEligiblePendingAsync(projectId, 10));
        Assert.Null(await sessions.LoadAsync(invocation.SessionId));
    }

    private async Task<IReadOnlyList<AgentJobLedgerRecord>> ListEligibleAgentJobsAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        return await jobs.ListEligiblePendingAsync(projectId, 10);
    }

    /// <summary>In-memory stand-in for the production agent identity preflight.</summary>
    public sealed class WorkflowAgentHandoffPreflightProbe : IWorkflowAgentHandoffPreflight
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string ProjectId, string AgentRef), AgentExecutionIdentitySnapshot> _definitions = [];
        private readonly Dictionary<(string ProjectId, string AgentRef), int> _resolveCounts = [];

        public void Set(
            string projectId,
            string agentRef,
            AgentExecutionDefinition definition,
            string? canonicalAgentId = null)
        {
            lock (_gate)
                _definitions[(projectId, agentRef)] = new(
                    canonicalAgentId ?? agentRef,
                    definition);
        }

        public int ResolveCount(string projectId, string agentRef)
        {
            lock (_gate)
                return _resolveCounts.GetValueOrDefault((projectId, agentRef));
        }

        public Task<WorkflowAgentPreflightResult> ResolveAgentAsync(string projectId, string agentRef)
        {
            lock (_gate)
            {
                var key = (projectId, agentRef);
                _resolveCounts[key] = _resolveCounts.GetValueOrDefault(key) + 1;
                var agent = _definitions.GetValueOrDefault(key);
                return Task.FromResult(agent is null
                    ? new WorkflowAgentPreflightResult(null, "agent_not_found", "Agent not found.")
                    : new WorkflowAgentPreflightResult(agent));
            }
        }
    }
}
