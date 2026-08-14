using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Orleans;
using Orleans.Core.Internal;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[CollectionDefinition("WorkflowAgentHandoff", DisableParallelization = true)]
public sealed class WorkflowAgentHandoffGrainCollection : ICollectionFixture<WorkflowAgentHandoffGrainFixture>;

/// <summary>
/// The handoff fence is intentionally exercised without a Runner. These
/// specs prove that durable preflight and acceptance do not accidentally
/// materialize a participant a Runner could claim.
/// </summary>
[Collection("WorkflowAgentHandoff")]
public sealed class WorkflowAgentHandoffGrainSpecs
{
    private readonly WorkflowAgentHandoffGrainFixture _fixture;

    public WorkflowAgentHandoffGrainSpecs(WorkflowAgentHandoffGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Preflight.Reset();
    }

    [Fact]
    public async Task PrepareAsync_ReplayAfterActivationLoss_ReusesFrozenGenericInvocationWithoutParticipants()
    {
        var projectId = $"workflow-handoff-replay-{Guid.NewGuid():N}";
        var agentId = $"agent_replay_{Guid.NewGuid():N}";
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "keep the original definition");
        var handoff = Handoff(command);

        var prepared = await handoff.PrepareAsync(command);

        Assert.Equal(WorkflowAgentHandoffDisposition.Prepared, prepared.Disposition);
        Assert.NotNull(prepared.Invocation);
        Assert.False(prepared.AlreadyPersisted);
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);

        await handoff.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.OpenCodeRuntime));

        var replay = await handoff.PrepareAsync(command);
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Prepared, replay.Disposition);
        Assert.True(replay.AlreadyPersisted);
        Assert.Equal(prepared.Invocation, replay.Invocation);
        Assert.NotNull(plan);
        Assert.Equal(AgentConfigSchema.PiRuntime, plan!.ExecutionDefinition!.Runtime);
        Assert.Equal(1, _fixture.Preflight.ResolveCount(projectId, agentId));
        await AssertNoParticipantsAsync(projectId, prepared.Invocation);
    }

    [Fact]
    public async Task PrepareAsync_ConflictingRenderedInput_PreservesFirstInvocation()
    {
        var projectId = $"workflow-handoff-conflict-{Guid.NewGuid():N}";
        var agentId = $"agent_conflict_{Guid.NewGuid():N}";
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var first = Command(projectId, agentId, "first rendered prompt");
        var handoff = Handoff(first);

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
        Assert.Equal(1, _fixture.Preflight.ResolveCount(projectId, agentId));
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task PrepareAsync_MissingAgent_PersistsPreflightRejection()
    {
        var projectId = $"workflow-handoff-rejection-{Guid.NewGuid():N}";
        var agentId = $"agent_missing_{Guid.NewGuid():N}";
        var command = Command(projectId, agentId, "must not start");
        var handoff = Handoff(command);

        var rejected = await handoff.PrepareAsync(command);
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
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
        Assert.Equal(1, _fixture.Preflight.ResolveCount(projectId, agentId));
        Assert.Empty(await ListEligibleAgentJobsAsync(projectId));
    }

    [Fact]
    public async Task PrepareAsync_RejectionReplayAfterActivationLoss_DoesNotRerunPreflight()
    {
        var projectId = $"workflow-handoff-rejection-replay-{Guid.NewGuid():N}";
        var agentId = $"agent_missing_replay_{Guid.NewGuid():N}";
        var command = Command(projectId, agentId, "persist the definitive rejection");
        var handoff = Handoff(command);

        var rejected = await handoff.PrepareAsync(command);
        await handoff.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));

        var replay = await handoff.PrepareAsync(command);
        var plan = await handoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, rejected.Disposition);
        Assert.Equal("agent_not_found", rejected.Rejection!.Code);
        Assert.Equal(WorkflowAgentHandoffDisposition.Rejected, replay.Disposition);
        Assert.True(replay.AlreadyPersisted);
        Assert.Equal(rejected.Rejection, replay.Rejection);
        Assert.Equal(1, _fixture.Preflight.ResolveCount(projectId, agentId));
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
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "await workflow acceptance");
        var handoff = Handoff(command);
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
        await AssertNoParticipantsAsync(projectId, prepared.Invocation!);
    }

    [Fact]
    public async Task AcceptAsync_ReplayAfterActivationLoss_ReusesThePersistedReceipt()
    {
        var projectId = $"workflow-handoff-accept-replay-{Guid.NewGuid():N}";
        var agentId = $"agent_accept_replay_{Guid.NewGuid():N}";
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "replay the acceptance receipt");
        var handoff = Handoff(command);
        var prepared = await handoff.PrepareAsync(command);
        var acceptance = new WorkflowAgentHandoffAcceptance(
            command.CommandId,
            WorkflowAgentHandoffCodec.Fingerprint(command));

        var accepted = await handoff.AcceptAsync(acceptance);
        var acceptedPlan = await handoff.GetPlanAsync();
        await handoff.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

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
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "accept the original prompt");
        var handoff = Handoff(command);
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
        _fixture.Preflight.Set(projectId, agentId, Definition(AgentConfigSchema.PiRuntime));
        var command = Command(projectId, agentId, "do not persist under another command");
        var wrongKey = WorkflowAgentHandoffCodec.KeyFor(
            projectId,
            command.WorkflowRunId,
            command.TaskRunId,
            $"other-{command.CommandId}");
        var handoff = _fixture.Grains.GetGrain<IWorkflowAgentHandoffGrain>(wrongKey);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handoff.PrepareAsync(command));

        Assert.Contains("grain key does not match", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _fixture.Preflight.ResolveCount(projectId, agentId));
    }

    private IWorkflowAgentHandoffGrain Handoff(WorkflowAgentHandoffCommand command) =>
        _fixture.Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(
                command.ProjectId,
                command.WorkflowRunId,
                command.TaskRunId,
                command.CommandId));

    private static WorkflowAgentHandoffCommand Command(
        string projectId,
        string agentId,
        string prompt) =>
        new(
            CommandId: $"workflow-work-{Guid.NewGuid():N}",
            ProjectId: projectId,
            WorkflowRunId: $"workflow-run-{Guid.NewGuid():N}",
            TaskRunId: $"task-run-{Guid.NewGuid():N}",
            AgentRef: agentId,
            Prompt: prompt,
            Session: "workflow-session",
            TimeoutMilliseconds: 60_000);

    private static AgentExecutionDefinition Definition(string runtime) =>
        new(
            Instructions: "follow the workflow task",
            Runtime: runtime,
            Model: "model-test",
            Variant: "high",
            Skills: []);

    private async Task AssertNoParticipantsAsync(string projectId, WorkflowAgentInvocation invocation)
    {
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();

        Assert.Null(await jobs.LoadLedgerAsync(invocation.JobKey));
        Assert.Empty(await jobs.ListEligiblePendingAsync(projectId, 10));
        Assert.Null(await sessions.LoadAsync(invocation.SessionId));
    }

    private async Task<IReadOnlyList<AgentJobLedgerRecord>> ListEligibleAgentJobsAsync(string projectId)
    {
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        return await jobs.ListEligiblePendingAsync(projectId, 10);
    }
}

public sealed class WorkflowAgentHandoffGrainFixture : IAsyncLifetime
{
    private readonly RecordingEventStore _eventStore = new();
    private readonly InMemoryEventBus _eventBus;
    private TestSqliteDatabase _database = null!;

    public WorkflowAgentHandoffGrainFixture()
    {
        _eventBus = new InMemoryEventBus(
            _eventStore,
            TimeProvider,
            NullLogger<InMemoryEventBus>.Instance);
    }

    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public FakeTimeProvider TimeProvider { get; } = new(
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public WorkflowAgentHandoffPreflightProbe Preflight { get; } = new();

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            GrainTestConfig.ConfigureSilo(
                siloBuilder,
                _database.ConnectionString,
                _eventBus,
                _eventStore,
                TimeProvider);
            siloBuilder.Services.AddSingleton<IWorkflowAgentHandoffPreflight>(Preflight);
        });
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public ValueTask DisposeAsync()
    {
        Cluster?.Dispose();
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class WorkflowAgentHandoffPreflightProbe : IWorkflowAgentHandoffPreflight
{
    private readonly object _gate = new();
    private readonly Dictionary<(string ProjectId, string AgentRef), AgentExecutionDefinition> _definitions = [];
    private readonly Dictionary<(string ProjectId, string AgentRef), int> _resolveCounts = [];

    public void Reset()
    {
        lock (_gate)
        {
            _definitions.Clear();
            _resolveCounts.Clear();
        }
    }

    public void Set(string projectId, string agentRef, AgentExecutionDefinition definition)
    {
        lock (_gate)
            _definitions[(projectId, agentRef)] = definition;
    }

    public int ResolveCount(string projectId, string agentRef)
    {
        lock (_gate)
            return _resolveCounts.GetValueOrDefault((projectId, agentRef));
    }

    public Task<AgentExecutionDefinition?> ResolveAgentAsync(string projectId, string agentRef)
    {
        lock (_gate)
        {
            var key = (projectId, agentRef);
            _resolveCounts[key] = _resolveCounts.GetValueOrDefault(key) + 1;
            return Task.FromResult<AgentExecutionDefinition?>(
                _definitions.GetValueOrDefault(key));
        }
    }
}
