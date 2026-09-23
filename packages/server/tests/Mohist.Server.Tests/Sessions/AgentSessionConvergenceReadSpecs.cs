using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

[Trait("level", "L0")]
public sealed class AgentSessionConvergenceReadSpecs
{
    [Fact]
    public async Task SettledUnknown_RemainsInspectableWithoutBlockingCurrentRecovery()
    {
        using var db = await UnifiedSessionSummaryFactory.BuildBareDbAsync();
        var session = await ConvergeAsync(db, RunnerSessionActivityObservations.Idle);
        await SaveAsync(db, session);

        var data = await ReadAsync(db);
        var previous = Assert.Single(data.GetProperty("unresolvedPrevious").EnumerateArray());
        Assert.Equal("old-turn", previous.GetProperty("id").GetString());
        Assert.Equal("unknown", previous.GetProperty("status").GetString());
        Assert.Equal(1, previous.GetProperty("contextGeneration").GetInt64());
        Assert.Equal(TestTime.UtcDateTime.ToString("o"), previous.GetProperty("supersededAt").GetString());
        Assert.Equal(1, data.GetProperty("unresolvedPreviousCount").GetInt32());
        Assert.Equal("inspect_previous_execution", data.GetProperty("nextAction").GetString());
        Assert.Equal("idle", data.GetProperty("activity").GetString());
        Assert.True(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.False(data.TryGetProperty("currentTurnId", out _));

        var generic = await UnifiedSessionSummaryFactory.CreateQuerier(db).GetGenericSessionSummaryAsync(
            UnifiedSessionSummaryFactory.ProjectA, session.Id);
        Assert.True(generic!.RecoveryAvailable);
        Assert.Equal("old-turn", Assert.Single(generic.UnresolvedPrevious!).Id);
        Assert.Equal(1, generic.UnresolvedPreviousCount);
    }

    [Fact]
    public async Task NewContext_PreservesPreviousInputGenerationAndReportsItsCurrentTurn()
    {
        using var db = await UnifiedSessionSummaryFactory.BuildBareDbAsync();
        var session = await ConvergeAsync(db, RunnerSessionActivityObservations.UnknownToRunner);
        session.RebindRuntimeSession(
            session.CurrentRuntimeBinding(),
            new AgentRuntimeBinding(session.Runtime.RunnerId, session.Runtime.Runtime, "runtime-new"),
            "missing-recovery", now: TestTime.UtcDateTime);
        var accepted = session.AcceptFollowup(
            "new-input", "new-turn", "new-operation", "continue", "agent-session-followup", "new-key", TestTime.UtcDateTime);
        session.MarkTurnExecuting(accepted.TurnId, TestTime.UtcDateTime);
        session.SetActivity(AgentSessionActivity.Active, TestTime.UtcDateTime);
        await SaveAsync(db, session);

        var data = await ReadAsync(db);
        Assert.Equal(2, data.GetProperty("contextGeneration").GetInt64());
        Assert.Equal("new-turn", data.GetProperty("currentTurnId").GetString());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("old-turn", Assert.Single(data.GetProperty("unresolvedPrevious").EnumerateArray())
            .GetProperty("id").GetString());
        var inputs = data.GetProperty("inputs").EnumerateArray().ToDictionary(input => input.GetProperty("id").GetString()!);
        Assert.Equal(1, inputs["old-input"].GetProperty("contextGeneration").GetInt64());
        Assert.Equal(2, inputs["new-input"].GetProperty("contextGeneration").GetInt64());
        var current = data.GetProperty("turns").EnumerateArray().Single(turn => turn.GetProperty("id").GetString() == "new-turn");
        Assert.Equal(2, current.GetProperty("contextGeneration").GetInt64());
    }

    [Fact]
    public async Task ConfirmedExecution_RemainsCurrentAheadOfLaterQueuedInput()
    {
        using var db = await UnifiedSessionSummaryFactory.BuildBareDbAsync();
        var session = await ConvergeAsync(db, RunnerSessionActivityObservations.Executing);
        session.AcceptFollowup(
            "queued-input", "queued-turn", "queued-operation", "later", "agent-session-followup", "queued-key", TestTime.UtcDateTime);
        await SaveAsync(db, session);

        var data = await ReadAsync(db);
        Assert.Equal("old-turn", data.GetProperty("currentTurnId").GetString());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal(0, data.GetProperty("unresolvedPreviousCount").GetInt32());
        Assert.DoesNotContain(data.GetProperty("turns").EnumerateArray(), turn => turn.TryGetProperty("supersededAt", out _));
    }

    private static async Task<AgentSession> ConvergeAsync(SummaryTestDb db, string observation)
    {
        await using var context = db.Factory.CreateDbContext();
        var row = await context.AgentSessions.SingleAsync(row => row.Id == UnifiedSessionSummaryFactory.AgentLaunchSession);
        var session = AgentSessionJson.Deserialize(row)!;
        session.Runtime = session.Runtime with { WorkDir = "/work" };
        session.EnsureInitialLaunch("initial-input", "initial-turn", "start", "agent-launch", "initial-job", TestTime.UtcDateTime);
        session.MarkInitialTurnExecuting("initial-job", TestTime.UtcDateTime);
        session.MarkInitialTurnTerminal("initial-job", AgentTurnStatus.Completed, null, TestTime.UtcDateTime);
        var accepted = session.AcceptFollowup(
            "old-input", "old-turn", "old-operation", "work", "agent-session-followup", "old-key", TestTime.UtcDateTime);
        session.MarkFollowupTurnExecuting(accepted.OperationId, TestTime.UtcDateTime);
        session.MarkFollowupTurnTerminal(accepted.OperationId, AgentTurnStatus.Unknown, null, TestTime.UtcDateTime);
        var capture = session.CaptureActivityObservation(session.Runtime.RunnerId, "probe", false, TestTime.UtcDateTime)!;
        var probe = new RunnerSessionActivityProbeRequest(
            session.Id, capture.ObservationId, capture.RunnerId, capture.Runtime, capture.RuntimeSessionId,
            capture.WorkDir, capture.BindingEpoch, capture.ContextGeneration);
        Assert.True(session.SettleActivityFromEvidence(new RunnerSessionActivityProbeResult(probe, observation), TestTime.UtcDateTime).Applied);
        return session;
    }

    private static async Task SaveAsync(SummaryTestDb db, AgentSession session)
    {
        await using var context = db.Factory.CreateDbContext();
        var row = await context.AgentSessions.SingleAsync(row => row.Id == session.Id);
        row.State = JsonSerializer.Serialize(session, JSON.Options);
        await context.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadAsync(SummaryTestDb db) =>
        await UnifiedSessionSummaryFactory.OkDataAsync(await UnifiedSessionRoutes.HandleShowAsync(
            UnifiedSessionSummaryFactory.ProjectAInfo,
            UnifiedSessionSummaryFactory.AgentLaunchSession,
            UnifiedSessionSummaryFactory.CreateQuerier(db),
            CancellationToken.None));
}
