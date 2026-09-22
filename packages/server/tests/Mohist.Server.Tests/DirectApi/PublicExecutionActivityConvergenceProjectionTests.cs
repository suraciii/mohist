using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.Tests.DirectApi;

[Trait("level", "L0")]
public sealed class PublicExecutionActivityConvergenceProjectionTests : IAsyncDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 9, 10, 15, 0, DateTimeKind.Utc);
    private readonly PublicProjectionTestSupport _support = new();

    [Fact]
    public async Task SupersededUnknownHistory_RemainsUnknownWithoutBlockingCurrentAdmission()
    {
        var terminalAt = new DateTimeOffset(T0.AddMinutes(1));
        await _support.SeedJobAsync(
            "job_settled_unknown",
            "proj_pub",
            "agent_pub",
            "session_settled_unknown",
            "input_settled_unknown",
            "turn_settled_unknown",
            status: AgentJobStatus.Unknown,
            terminalResult: new AgentJobTerminalResult(
                AgentJobStatus.Unknown,
                "activity-converged:idle",
                null,
                null,
                "activity-converged:idle",
                null),
            terminalAt: terminalAt);

        var supersededAt = T0.AddMinutes(1);
        await _support.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _support.BuildSession("session_settled_unknown", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs:
            [
                PublicProjectionTestSupport.Input("input_settled_unknown", "job_settled_unknown", recordedAt: T0),
                PublicProjectionTestSupport.Input("input_current", null, recordedAt: T0.AddMinutes(2)),
            ],
            turns:
            [
                PublicProjectionTestSupport.Turn(
                    "turn_settled_unknown",
                    "input_settled_unknown",
                    "job_settled_unknown",
                    AgentTurnStatus.Unknown,
                    recordedAt: T0,
                    updatedAt: supersededAt,
                    supersededAt: supersededAt),
                PublicProjectionTestSupport.Turn(
                    "turn_current",
                    "input_current",
                    null,
                    AgentTurnStatus.Executing,
                    recordedAt: T0.AddMinutes(2)),
            ]));

        Assert.True(await _support.Engine.ProcessPendingAsync());

        var historicalTurn = await SnapshotAsync("turn", "turn_settled_unknown");
        Assert.Equal(PublicExecutionFieldValues.StatusUnknown, historicalTurn.Status);
        Assert.Equal(PublicExecutionFieldValues.TurnUnknown, historicalTurn.TurnStatus);
        Assert.Equal(PublicExecutionFieldValues.JobUnknown, historicalTurn.JobStatus);
        Assert.Equal(PublicExecutionFieldValues.AdmissionReady, historicalTurn.Admission);
        Assert.Null(historicalTurn.TerminalAt);

        var historicalJob = await SnapshotAsync("job", "job_settled_unknown");
        Assert.Equal(PublicExecutionFieldValues.StatusUnknown, historicalJob.Status);
        Assert.Equal(PublicExecutionFieldValues.AdmissionReady, historicalJob.Admission);

        var current = await SnapshotAsync("turn", "turn_current");
        Assert.Equal(PublicExecutionFieldValues.StatusRunning, current.Status);
        Assert.Equal("turn_current", current.TurnId);
        Assert.Equal(PublicExecutionFieldValues.AdmissionReady, current.Admission);

        var read = await new PublicExecutionReadQuerier(_support.DbFactory)
            .ReadTurnAsync("proj_pub", "turn_settled_unknown");
        Assert.Equal(PublicReadStatus.Found, read.Status);
        Assert.Equal(PublicExecutionFieldValues.AdmissionReady,
            JsonSerializer.Deserialize<PublicExecutionRead>(read.SnapshotJson!, JSON.PublicApi)!.Admission);
    }

    [Fact]
    public async Task CurrentUnknown_RemainsAdmissionBlocking()
    {
        await _support.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _support.BuildSession("session_current_unknown", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_current_unknown", null)],
            turns: [PublicProjectionTestSupport.Turn(
                "turn_current_unknown",
                "input_current_unknown",
                null,
                AgentTurnStatus.Unknown)]));

        Assert.True(await _support.Engine.ProcessPendingAsync());
        var current = await SnapshotAsync("turn", "turn_current_unknown");
        Assert.Equal(PublicExecutionFieldValues.StatusUnknown, current.Status);
        Assert.Equal(PublicExecutionFieldValues.AdmissionBlocked, current.Admission);
    }

    private async Task<PublicExecutionRead> SnapshotAsync(string anchorType, string anchorId) =>
        JsonSerializer.Deserialize<PublicExecutionRead>(
            (await _support.SnapshotAsync(anchorType, anchorId))!.SnapshotJson,
            JSON.PublicApi)!;

    public ValueTask DisposeAsync() => _support.DisposeAsync();
}
