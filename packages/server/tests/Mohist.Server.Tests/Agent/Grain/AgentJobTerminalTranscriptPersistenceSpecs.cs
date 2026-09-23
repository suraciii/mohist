using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Tests.Agent.Grain;

[Collection("AgentJobGrain")]
[Trait("level", "L1")]
public class AgentJobTerminalTranscriptPersistenceSpecs : AgentJobGrainTestSupport
{
    public AgentJobTerminalTranscriptPersistenceSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task ReportResultAsync_TerminalTranscriptFailure_RetainsPendingCloseUntilRedeliveryPersistsIt()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-terminal-retry-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-terminal-retry-{Guid.NewGuid():N}";
        var sessionId = $"session-terminal-retry-{Guid.NewGuid():N}";
        var inputId = $"input-terminal-retry-{Guid.NewGuid():N}";
        var turnId = $"turn-terminal-retry-{Guid.NewGuid():N}";
        await OpenJobSessionAsync(sessionId, projectId, jobKey, inputId, turnId, "do a failing thing");

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do a failing thing",
            WorkspacePath: "/tmp/agent-job-terminal-retry",
            ProjectId: projectId,
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId,
            AgentId: "agent-test"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        _fixture.SessionPersistence.QueueFailures(100);
        await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult(Status: "failed", Message: "transient", Output: JSON.DeserializeElement("{}"), ExitCode: 1));

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));
        Assert.True((await job.GetRuntimeSnapshotAsync()).HasPendingSessionClose);

        _fixture.SessionPersistence.ResetFailures();
        var replay = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult(Status: "failed", Message: "transient", Output: JSON.DeserializeElement("{}"), ExitCode: 1));

        Assert.True(replay.Accepted);
        Assert.Null(replay.Reason);
        Assert.False((await job.GetRuntimeSnapshotAsync()).HasPendingSessionClose);

        var persisted = Assert.Single(
            await ListTranscriptPartsAsync(sessionId),
            part => part.Type == TranscriptPartTypes.SessionActivity);
        Assert.Equal(AgentJobSessionDeliveryIds.TerminalDeliveryId(jobKey), persisted.CorrelationKey);
    }

    private async Task<List<AgentSessionTranscriptPartRow>> ListTranscriptPartsAsync(string sessionId)
    {
        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var turnIds = await db.AgentSessionTranscriptTurns
            .Where(turn => turn.SessionId == sessionId)
            .Select(turn => turn.Id)
            .ToListAsync();
        return await db.AgentSessionTranscriptParts
            .Where(part => turnIds.Contains(part.TurnId))
            .OrderBy(part => part.Sequence)
            .ThenBy(part => part.Id)
            .ToListAsync();
    }
}
