using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionScheduleGrainSpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public AgentSessionScheduleGrainSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task CreateSchedule_PersistsDurableChildRecord()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("create-persist");
        var dueAt = _fixture.TimeProvider.GetUtcNow().AddHours(1);

        var created = await grain.CreateScheduleAsync(CreateCommand("hello at noon", dueAt));

        Assert.False(string.IsNullOrWhiteSpace(created.Schedule.ScheduleId));
        Assert.False(created.AlreadyExists);
        Assert.Equal(SessionScheduleStatus.Scheduled, created.Schedule.Status);
        Assert.Equal("hello at noon", created.Schedule.Text);
        Assert.Equal("key-1", created.Schedule.IdempotencyKey);
        Assert.Equal(dueAt.UtcDateTime, created.Schedule.DueAt);

        var state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        var persisted = Assert.Single(state.Status.Schedules!);
        Assert.Equal(created.Schedule.ScheduleId, persisted.ScheduleId);
        Assert.Equal(SessionScheduleStatus.Scheduled, persisted.Status);
        Assert.Null(persisted.InputId);
    }

    [Fact]
    public async Task CreateSchedule_RejectsDueInPast()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("due-past");

        var ex = await Assert.ThrowsAsync<ScheduleDueInPastException>(() =>
            grain.CreateScheduleAsync(CreateCommand("too late", _fixture.TimeProvider.GetUtcNow().AddHours(-1))));

        Assert.Equal(sessionId, ex.SessionId);
    }

    [Fact]
    public async Task CreateSchedule_ReplayWithSameKeyAndNormalizedBodyReturnsOriginal()
    {
        var (grain, _) = await CreateAttachedSessionAsync("replay-same");
        var dueAt = _fixture.TimeProvider.GetUtcNow().AddHours(1);
        var first = await grain.CreateScheduleAsync(CreateCommand("  hello at noon  ", dueAt, "replay-key"));

        var replay = await grain.CreateScheduleAsync(CreateCommand("hello at noon", dueAt, "replay-key"));

        Assert.True(replay.AlreadyExists);
        Assert.Equal(first.Schedule.ScheduleId, replay.Schedule.ScheduleId);
        Assert.Equal(first.Schedule.Text, replay.Schedule.Text);
        Assert.Equal(first.Schedule.DueAt, replay.Schedule.DueAt);
        Assert.Equal(first.Schedule.Status, replay.Schedule.Status);
    }

    [Fact]
    public async Task CreateSchedule_SameKeyDifferentTextConflicts()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("replay-text-conflict");
        var dueAt = _fixture.TimeProvider.GetUtcNow().AddHours(1);
        await grain.CreateScheduleAsync(CreateCommand("first text", dueAt, "shared-key"));

        var ex = await Assert.ThrowsAsync<ScheduleIdempotencyConflictException>(() =>
            grain.CreateScheduleAsync(CreateCommand("second text", dueAt, "shared-key")));

        Assert.Equal(sessionId, ex.SessionId);
        Assert.Equal("shared-key", ex.IdempotencyKey);
    }

    [Fact]
    public async Task CreateSchedule_SameKeyDifferentDueAtConflicts()
    {
        var (grain, _) = await CreateAttachedSessionAsync("replay-due-conflict");
        var dueAt = _fixture.TimeProvider.GetUtcNow().AddHours(1);
        await grain.CreateScheduleAsync(CreateCommand("same text", dueAt, "shared-key"));

        await Assert.ThrowsAsync<ScheduleIdempotencyConflictException>(() =>
            grain.CreateScheduleAsync(CreateCommand("same text", dueAt.AddHours(1), "shared-key")));
    }

    [Fact]
    public async Task CreateSchedule_OmittedKeyMintsFreshKeyPerCall()
    {
        var (grain, _) = await CreateAttachedSessionAsync("omitted-key");
        var dueAt = _fixture.TimeProvider.GetUtcNow().AddHours(1);

        var first = await grain.CreateScheduleAsync(CreateCommand("first", dueAt, null));
        var second = await grain.CreateScheduleAsync(CreateCommand("second", dueAt, null));

        Assert.False(first.AlreadyExists);
        Assert.False(second.AlreadyExists);
        Assert.NotEqual(first.Schedule.ScheduleId, second.Schedule.ScheduleId);
        Assert.NotEqual(first.Schedule.IdempotencyKey, second.Schedule.IdempotencyKey);
    }

    [Fact]
    public async Task CancelSchedule_ScheduledAdvancesToCancelledAndReplayIsIdempotent()
    {
        var (grain, _) = await CreateAttachedSessionAsync("cancel-scheduled");
        var created = await grain.CreateScheduleAsync(CreateCommand("cancel me", _fixture.TimeProvider.GetUtcNow().AddHours(1)));

        var cancelled = await grain.CancelScheduleAsync(new CancelSessionScheduleCommand(created.Schedule.ScheduleId));

        Assert.False(cancelled.AlreadyTerminal);
        Assert.Equal(SessionScheduleStatus.Cancelled, cancelled.Schedule.Status);
        Assert.NotNull(cancelled.Schedule.CancelledAt);
        Assert.Null(cancelled.Schedule.InputId);

        var replay = await grain.CancelScheduleAsync(new CancelSessionScheduleCommand(created.Schedule.ScheduleId));

        Assert.True(replay.AlreadyTerminal);
        Assert.Equal(SessionScheduleStatus.Cancelled, replay.Schedule.Status);
        Assert.Equal(cancelled.Schedule.CancelledAt, replay.Schedule.CancelledAt);
    }

    [Fact]
    public async Task CancelSchedule_UnknownScheduleIdThrows()
    {
        var (grain, _) = await CreateAttachedSessionAsync("cancel-unknown");

        await Assert.ThrowsAsync<ScheduleNotFoundException>(() =>
            grain.CancelScheduleAsync(new CancelSessionScheduleCommand("no-such-schedule")));
    }

    [Fact]
    public async Task CancelSchedule_DeliveredStaysTerminalWithInputId()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("cancel-delivered");
        var created = await grain.CreateScheduleAsync(CreateCommand("already delivered", _fixture.TimeProvider.GetUtcNow().AddHours(1)));
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(2));
        await grain.RunScheduledInputRecoveryAsync();
        var state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        var inputId = Assert.IsType<SessionScheduleRecord>(state.FindSchedule(created.Schedule.ScheduleId)).InputId;
        Assert.False(string.IsNullOrWhiteSpace(inputId));

        var cancelled = await grain.CancelScheduleAsync(new CancelSessionScheduleCommand(created.Schedule.ScheduleId));

        Assert.True(cancelled.AlreadyTerminal);
        Assert.Equal(SessionScheduleStatus.Delivered, cancelled.Schedule.Status);
        Assert.Equal(inputId, cancelled.Schedule.InputId);
        Assert.Null(cancelled.Schedule.CancelledAt);
    }

    [Fact]
    public async Task RecoveryTick_DeliversDueScheduleExactlyOnce()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("recovery-deliver");
        var created = await grain.CreateScheduleAsync(CreateCommand("wake up", _fixture.TimeProvider.GetUtcNow().AddHours(1)));
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(2));

        await grain.RunScheduledInputRecoveryAsync();
        await grain.RunScheduledInputRecoveryAsync();

        var state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        var delivered = Assert.IsType<SessionScheduleRecord>(state.FindSchedule(created.Schedule.ScheduleId));
        Assert.Equal(SessionScheduleStatus.Delivered, delivered.Status);
        Assert.False(string.IsNullOrWhiteSpace(delivered.InputId));
        Assert.Null(delivered.CancelledAt);

        var input = Assert.Single(state.Status.Inputs!);
        Assert.Equal("session-schedule", input.Source);
        Assert.Equal("wake up", input.Text);
        Assert.Equal($"schedule:{created.Schedule.ScheduleId}", input.IdempotencyKey);
        Assert.Equal(delivered.InputId, input.Id);

        var turn = Assert.Single(state.Status.Turns!);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Equal(delivered.InputId, Assert.Single(turn.InputIds));
    }

    [Fact]
    public async Task RecoveryTick_NotDueStaysScheduled()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("recovery-not-due");
        var created = await grain.CreateScheduleAsync(CreateCommand("later", _fixture.TimeProvider.GetUtcNow().AddHours(1)));

        await grain.RunScheduledInputRecoveryAsync();

        var state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        Assert.Equal(SessionScheduleStatus.Scheduled, state.FindSchedule(created.Schedule.ScheduleId)!.Status);
        Assert.Empty(state.Status.Inputs ?? []);
    }

    [Fact]
    public async Task RecoveryTick_BlockedWithoutBindingStaysPendingThenDeliversAfterAttach()
    {
        var (grain, sessionId) = await CreateSessionAsync("recovery-missing-binding");
        var created = await grain.CreateScheduleAsync(CreateCommand("needs binding", _fixture.TimeProvider.GetUtcNow().AddHours(1)));
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(2));

        await grain.RunScheduledInputRecoveryAsync();

        var state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        Assert.Equal(SessionScheduleStatus.PendingDelivery, state.FindSchedule(created.Schedule.ScheduleId)!.Status);
        Assert.Empty(state.Status.Inputs ?? []);

        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-for-recovery"));
        await grain.RunScheduledInputRecoveryAsync();

        state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        Assert.Equal(SessionScheduleStatus.Delivered, state.FindSchedule(created.Schedule.ScheduleId)!.Status);
        Assert.Single(state.Status.Inputs!);
    }

    [Fact]
    public async Task CancelSchedule_PendingDeliveryCancelStopsFutureDelivery()
    {
        var (grain, sessionId) = await CreateSessionAsync("cancel-pending");
        var created = await grain.CreateScheduleAsync(CreateCommand("cancel before delivery", _fixture.TimeProvider.GetUtcNow().AddHours(1)));
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(2));

        await grain.RunScheduledInputRecoveryAsync();
        var pending = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        Assert.Equal(SessionScheduleStatus.PendingDelivery, pending.FindSchedule(created.Schedule.ScheduleId)!.Status);

        var cancelled = await grain.CancelScheduleAsync(new CancelSessionScheduleCommand(created.Schedule.ScheduleId));
        Assert.Equal(SessionScheduleStatus.Cancelled, cancelled.Schedule.Status);

        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-after-cancel"));
        await grain.RunScheduledInputRecoveryAsync();

        var final = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        Assert.Equal(SessionScheduleStatus.Cancelled, final.FindSchedule(created.Schedule.ScheduleId)!.Status);
        Assert.Empty(final.Status.Inputs ?? []);
    }

    private async Task<AgentSession?> LoadAsync(string sessionId) =>
        await _fixture.StateStore.LoadAsync(sessionId);

    private async Task<(IAgentSessionGrain Grain, string SessionId)> CreateAttachedSessionAsync(string name)
    {
        var (grain, sessionId) = await CreateSessionAsync(name);
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand($"runtime-{name}"));
        return (grain, sessionId);
    }

    private async Task<(IAgentSessionGrain Grain, string SessionId)> CreateSessionAsync(string name)
    {
        var sessionId = $"schedule-grain-{name}-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        return (grain, sessionId);
    }

    private CreateSessionScheduleCommand CreateCommand(string text, DateTimeOffset dueAt, string? key = "key-1") => new(
        text,
        dueAt,
        key);

    private static OpenAgentSessionCommand OpenCommand() => new(
        "runner-1",
        "opencode",
        WorkDir: "/work",
        Metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build"));
}
