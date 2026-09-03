using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Sessions;

public sealed partial class AgentSessionScheduleGrainSpecs
{
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

    [Fact]
    public async Task RecoveryTick_ActiveSession_AppendsQueuedTurnLikeOrdinaryFollowup()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("recovery-active");
        await grain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-existing",
            "turn-existing",
            "existing work",
            "generic-followup"));
        await grain.MarkTurnExecutingAsync("turn-existing");
        var created = await grain.CreateScheduleAsync(CreateCommand("wake while busy", _fixture.TimeProvider.GetUtcNow().AddHours(1)));
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(2));

        await grain.RunScheduledInputRecoveryAsync();

        var state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        var delivered = Assert.IsType<SessionScheduleRecord>(state.FindSchedule(created.Schedule.ScheduleId));
        Assert.Equal(SessionScheduleStatus.Delivered, delivered.Status);
        var scheduleInput = state.Status.Inputs!.Single(input => input.Id == delivered.InputId);
        Assert.Equal("session-schedule", scheduleInput.Source);
        var scheduleTurn = state.Status.Turns!.Single(turn => turn.InputIds.Contains(delivered.InputId!));
        Assert.Equal(AgentTurnStatus.Queued, scheduleTurn.Status);
        Assert.Equal(AgentTurnStatus.Executing, state.Status.Turns!.Single(turn => turn.Id == "turn-existing").Status);
    }

    [Fact]
    public async Task RecoveryTick_UnknownActivity_StaysPendingThenDeliversAfterEvidence()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("recovery-unknown");
        await grain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-unknown",
            "turn-unknown",
            "unknown work",
            "generic-followup"));
        await grain.MarkTurnExecutingAsync("turn-unknown");
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"unknown\",\"status\":\"failed\",\"turnId\":\"turn-unknown\"}") },
            "runtime-recovery-unknown"));
        var created = await grain.CreateScheduleAsync(CreateCommand("wait for evidence", _fixture.TimeProvider.GetUtcNow().AddHours(1)));
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(2));

        await grain.RunScheduledInputRecoveryAsync();

        var state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        var pending = Assert.IsType<SessionScheduleRecord>(state.FindSchedule(created.Schedule.ScheduleId));
        Assert.Equal(SessionScheduleStatus.PendingDelivery, pending.Status);
        Assert.DoesNotContain(state.Status.Inputs ?? [], input => input.Source == "session-schedule");

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"turn-unknown\"}") },
            "runtime-recovery-unknown"));
        await grain.RunScheduledInputRecoveryAsync();

        state = Assert.IsType<AgentSession>(await LoadAsync(sessionId));
        Assert.Equal(SessionScheduleStatus.Delivered, state.FindSchedule(created.Schedule.ScheduleId)!.Status);
        Assert.Single(state.Status.Inputs!, input => input.Source == "session-schedule");
    }
}
