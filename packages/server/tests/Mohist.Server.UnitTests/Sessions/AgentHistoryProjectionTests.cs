using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class AgentHistoryProjectionTests
{
    [Fact]
    public void Project_UsesCanonicalIdentityAndReadFactsForSummary()
    {
        var started = new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc);
        var ended = started.AddSeconds(12);
        var session = AgentSession.Create(
            "session-canonical",
            "runner-1",
            "/private/worktree",
            GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                "project-1",
                "agent-1",
                "Reviewer",
                IssueNumber: 385,
                Repository: "suraciii/mohist",
                WorkspaceName: "review",
                WorkspacePath: "/private/worktree",
                TargetId: "target-1")),
            started,
            "opencode");
        session.Settings = new AgentSessionSettings("configured-model");
        session.Status = session.Status with
        {
            UsageSummary = new AgentUsageSummary(CostAmount: 1.25, CostCurrency: "USD"),
            Inputs = [new AgentSessionInputRecord(
                "input-canonical",
                1,
                "Review the history contract",
                "agent-launch",
                AgentSessionInputAcceptance.Accepted,
                started,
                JobId: "job-canonical")],
            Turns = [new AgentTurnRecord(
                "turn-canonical",
                1,
                ["input-canonical"],
                AgentTurnStatus.Completed,
                JobId: "job-canonical",
                Result: new AgentTurnResult(Message: "Done", Output: "result"),
                RecordedAt: started,
                UpdatedAt: ended)],
        };

        var record = new AgentSessionRecord(
            new AgentSessionRow { Id = session.Id, CreatedAt = started },
            session,
            session.Metadata.Labels!);
        var items = AgentHistoryProjector.Project(new AgentHistoryProjectionSource(
            record,
            new AgentSessionTranscriptData(
                [new AgentSessionTranscriptTurnRow
                {
                    Id = 91,
                    SessionId = session.Id,
                    Sequence = 1,
                    StartedAt = started.AddSeconds(-1),
                    UpdatedAt = ended,
                }],
                []),
            "resolved-model"));

        var item = Assert.Single(items);
        Assert.Equal("turn-canonical", item.Id);
        Assert.Equal("session-canonical", item.SessionId);
        Assert.Equal("input-canonical", item.InputId);
        Assert.Equal(["input-canonical"], item.InputIds);
        Assert.Equal("turn-canonical", item.TurnId);
        Assert.Equal("job-canonical", item.JobId);
        Assert.Equal("Review the history contract", item.Task);
        Assert.Equal("completed", item.Status);
        Assert.Equal("success", item.Outcome);
        Assert.Equal("resolved-model", item.Model);
        Assert.Equal(12000, item.DurationMs);
        Assert.Equal(1.25, item.Cost.Amount);
        Assert.Equal("USD", item.Cost.Currency);
        Assert.Equal("session", item.Cost.Scope);
        Assert.Equal(385, item.Context!.IssueNumber);
        Assert.Equal("suraciii/mohist", item.Context.Repository);
        Assert.Equal("review", item.Workspace);
        Assert.Equal("target-1", item.Target);
        Assert.DoesNotContain("private", item.Task, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reduce_DeduplicatesCanonicalTurnAndSeparatesRecentFromEnded()
    {
        var rows = new[]
        {
            Item("s-1", "t-1", "completed", "2026-08-09T10:00:00.0000000Z", result: null),
            Item("s-1", "t-1", "completed", "2026-08-09T10:00:00.0000000Z", result: new AgentTurnResultObservationDto("done", null, null, null, 0)),
            Item("s-2", "t-2", "completed", "2026-08-09T09:00:00.0000000Z"),
            Item("s-3", "t-3", "failed", "2026-08-09T11:00:00.0000000Z"),
            Item("s-4", "t-4", "executing", "2026-08-09T12:00:00.0000000Z"),
            Item("s-5", "t-5", "completed", "2026-08-09T08:00:00.0000000Z"),
            Item("s-6", "t-6", "unknown", "2026-08-09T07:00:00.0000000Z"),
        };

        var result = AgentHistoryBucketReducer.Reduce(rows, recentLimit: 2);

        Assert.Equal(6, result.Count);
        Assert.Equal(6, result.Select(item => (item.SessionId, item.TurnId)).Distinct().Count());
        Assert.Equal("done", result.Single(item => item.TurnId == "t-1").Result!.Message);
        Assert.Equal("running", result.Single(item => item.TurnId == "t-4").Bucket);
        Assert.Equal("failed", result.Single(item => item.TurnId == "t-3").Bucket);
        Assert.Equal("recent", result.Single(item => item.TurnId == "t-1").Bucket);
        Assert.Equal("recent", result.Single(item => item.TurnId == "t-2").Bucket);
        Assert.Equal("ended", result.Single(item => item.TurnId == "t-5").Bucket);
        Assert.Equal("unknown", result.Single(item => item.TurnId == "t-6").Bucket);
        Assert.Empty(result.Where(item => item.Bucket == "ended")
            .Intersect(result.Where(item => item.Bucket == "recent")));
    }

    private static AgentHistoryItemDto Item(
        string sessionId,
        string turnId,
        string status,
        string startedAt,
        AgentTurnResultObservationDto? result = null) =>
        new(
            turnId,
            sessionId,
            "input-1",
            ["input-1"],
            turnId,
            null,
            "task",
            null,
            status,
            status == "completed" ? "success" : "failure",
            result,
            startedAt,
            status == "executing" ? null : startedAt,
            status == "executing" ? null : 1,
            "model",
            new AgentHistoryCostDto(null, null, "session"),
            null,
            null,
            "recent");
}
