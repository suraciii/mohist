using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.Tests.Agent.Grain;

[Trait("level", "L0")]
public sealed class AgentLaunchCoordinatorReplayTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcceptedPlan_ReplaysStoredIdentityWithoutRelaunch_AndChangedHintConflicts()
    {
        var request = Request();
        var plan = CompletedPlan(request);
        var state = new FakePersistentState(new AgentLaunchCoordinatorState { Plan = plan });
        var coordinator = CreateCoordinator(state);

        var replay = await coordinator.ResumeAsync(request);

        Assert.NotNull(replay);
        Assert.Equal(plan.JobKey, replay.JobKey);
        Assert.Equal(plan.SessionId, replay.SessionId);
        Assert.Equal(plan.InputId, replay.InputId);
        Assert.Equal(plan.TurnId, replay.TurnId);
        Assert.Equal(plan.AgentId, replay.AgentId);
        Assert.Equal(plan.AgentName, replay.AgentName);
        Assert.Equal(plan.WorkspaceName, replay.WorkspaceName);
        Assert.Equal(plan.Origin, replay.Origin);
        Assert.Equal(plan.TargetId, replay.TargetId);
        Assert.True(replay.AlreadyPersisted);
        Assert.Same(plan, state.State.Plan);
        Assert.True(state.RecordExists);
        Assert.Equal(0, state.WriteCount);
        Assert.Equal(0, state.ClearCount);

        var conflict = await Assert.ThrowsAsync<LaunchIdempotencyConflictException>(
            () => coordinator.ResumeAsync(request with { Model = "provider/changed" }));
        Assert.Equal(plan.IdempotencyKey, conflict.IdempotencyKey);
        Assert.Equal(plan.RequestFingerprint, conflict.ExistingFingerprint);
        Assert.Same(plan, state.State.Plan);
        Assert.True(state.RecordExists);
        Assert.Equal(0, state.WriteCount);
        Assert.Equal(0, state.ClearCount);
    }

    [Fact]
    public async Task TerminalRejection_ReplaysStoredOutcomeWithoutPreflight_AndChangedFingerprintConflicts()
    {
        const string rejectionReason = "simulated_terminal_rejection";
        var request = Request();
        var plan = CompletedPlan(request) with
        {
            RejectionReason = rejectionReason,
            DefinitionCreatedByLaunch = true,
        };
        var state = new FakePersistentState(new AgentLaunchCoordinatorState { Plan = plan });
        var coordinator = CreateCoordinator(state);

        var replay = await Assert.ThrowsAsync<AgentSpawnPreplanRejectedException>(
            () => coordinator.ResumeAsync(request));
        Assert.Equal(rejectionReason, replay.Reason);
        Assert.Same(plan, state.State.Plan);
        Assert.True(state.RecordExists);
        Assert.Equal(0, state.WriteCount);
        Assert.Equal(0, state.ClearCount);

        var conflict = await Assert.ThrowsAsync<LaunchIdempotencyConflictException>(
            () => coordinator.ResumeAsync(request with { Variant = "high" }));
        Assert.Equal(plan.IdempotencyKey, conflict.IdempotencyKey);
        Assert.Equal(plan.RequestFingerprint, conflict.ExistingFingerprint);
        Assert.Same(plan, state.State.Plan);
        Assert.True(state.RecordExists);
        Assert.Equal(0, state.WriteCount);
        Assert.Equal(0, state.ClearCount);
    }

    private static AgentLaunchCoordinatorGrain CreateCoordinator(
        IPersistentState<AgentLaunchCoordinatorState> state) =>
        new(
            state,
            grains: null!,
            new FakeTimeProvider(Fixed),
            participantProbe: null!,
            NullLogger<AgentLaunchCoordinatorGrain>.Instance);

    private static AgentLaunchCoordinatorRequest Request() => new(
        Prompt: "Implement the task-first route",
        AgentRef: "task-route-agent",
        Runtime: "pi",
        WorkspacePath: "/workspace",
        IssueNumber: null,
        EpicNumber: null,
        Repository: "main",
        Title: null,
        Model: "provider/task",
        Variant: "balanced");

    private static AgentLaunchCoordinatorPlan CompletedPlan(AgentLaunchCoordinatorRequest request) => new(
        ProjectId: "project-1",
        IdempotencyKey: "task-launch-replay",
        RequestFingerprint: AgentLaunchCoordinatorCodec.Fingerprint(request),
        JobKey: "agent-job-launch-1",
        SessionId: "agent-session-1",
        InputId: "input-1",
        TurnId: "turn-1",
        AgentId: "agent-1",
        AgentName: "task-route-agent",
        AgentInstructions: "Complete the task.",
        AgentConfigJson: null,
        Model: "provider/task",
        Variant: "balanced",
        Runtime: "pi",
        Prompt: request.Prompt,
        WorkspaceName: "workspace-1",
        WorkspacePath: request.WorkspacePath,
        IssueNumber: request.IssueNumber,
        EpicNumber: request.EpicNumber,
        Repository: request.Repository,
        Title: request.Title,
        AgentRef: request.AgentRef,
        Completed: true,
        Origin: "web",
        TargetId: "agent-1");

    private sealed class FakePersistentState(AgentLaunchCoordinatorState state)
        : IPersistentState<AgentLaunchCoordinatorState>
    {
        public AgentLaunchCoordinatorState State { get; set; } = state;
        public string Etag { get; set; } = "1";
        public bool RecordExists { get; private set; } = true;
        public string StateName => "agent-launch-coordinator";
        public string StorageName => "test";
        public int WriteCount { get; private set; }
        public int ClearCount { get; private set; }

        public Task ClearStateAsync()
        {
            ClearCount++;
            RecordExists = false;
            return Task.CompletedTask;
        }

        public Task ReadStateAsync() => Task.CompletedTask;

        public Task WriteStateAsync()
        {
            WriteCount++;
            RecordExists = true;
            return Task.CompletedTask;
        }
    }
}
