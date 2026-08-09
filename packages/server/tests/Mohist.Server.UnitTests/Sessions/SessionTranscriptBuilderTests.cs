using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class SessionTranscriptBuilderTests
{
    private static AgentSession CreateSession(DateTime at, string workDir = "/workspace") => AgentSession.Create(
        "session-1",
        "runner-1",
        workDir,
        metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "transcript"),
        now: at,
        runtime: "opencode");

    [Fact]
    public void Build_UsesCanonicalInputAndAppliesTheSameSensitiveBoundaryToRaw()
    {
        var at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = CreateSession(at);
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            Inputs = [new AgentSessionInputRecord(
                "input-1", 1, "review the change", "agent-launch",
                AgentSessionInputAcceptance.Accepted, at, JobId: "job-1")],
            Turns = [new AgentTurnRecord(
                "turn-1", 1, ["input-1"], AgentTurnStatus.Queued, JobId: "job-1")],
        };
        var turn = new AgentSessionTranscriptTurnRow
        {
            Id = 1,
            SessionId = session.Id,
            RuntimeSessionId = "runtime-1",
            Sequence = 1,
            PromptText = "[mohist-workspace-anchor]\n/workspace\n[/mohist-workspace-anchor]\n\ninternal instructions\n\nreview the change",
            PromptKind = "task",
            StartedAt = at,
            UpdatedAt = at,
        };
        var toolPayload = "{\"toolCallId\":\"tool-1\",\"kind\":\"read\",\"status\":\"completed\",\"title\":\"Read file\",\"rawInput\":{\"filePath\":\"/workspace/secret.txt\"},\"rawOutput\":{\"content\":\"ok\"}}";
        var part = new AgentSessionTranscriptPartRow
        {
            Id = 1,
            TurnId = turn.Id,
            Sequence = 1,
            Type = TranscriptPartTypes.Tool,
            PayloadJson = toolPayload,
            FirstSeenAt = at,
            LastSeenAt = at,
        };

        var response = SessionTranscriptBuilder.Build(new AgentSessionTranscriptData([turn], [part]), session);
        var resultTurn = Assert.Single(response.Turns);
        var tool = Assert.Single(resultTurn.Assistant).Tool!;

        Assert.Equal("review the change", resultTurn.User.Text);
        Assert.Equal("queued", resultTurn.Status);
        Assert.Equal("active", response.Activity);
        Assert.Equal("queued", response.Status);
        Assert.Null(tool.Input);
        Assert.Null(tool.Output);
        Assert.Null(tool.RawInput);
        Assert.Null(tool.RawOutput);

        var diagnostic = SessionTranscriptBuilder.Build(new AgentSessionTranscriptData([turn], [part]), session, "raw");
        var diagnosticTurn = Assert.Single(diagnostic.Turns);
        var diagnosticTool = Assert.Single(diagnosticTurn.Assistant).Tool!;
        Assert.DoesNotContain("mohist-workspace-anchor", diagnosticTurn.User.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/workspace/secret.txt", diagnosticTool.RawInput, StringComparison.Ordinal);
        Assert.DoesNotContain("/workspace/secret.txt", diagnosticTool.Input, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnknownPartIsPublicWithoutPayloadAndRawIsExplicitDiagnostic()
    {
        var at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = CreateSession(at);
        var turn = new AgentSessionTranscriptTurnRow
        {
            Id = 1,
            SessionId = session.Id,
            Sequence = 1,
            PromptText = "inspect the event",
            PromptKind = "task",
            StartedAt = at,
            UpdatedAt = at,
        };
        var part = new AgentSessionTranscriptPartRow
        {
            Id = 7,
            TurnId = turn.Id,
            Sequence = 1,
            Type = "future.runtime.event",
            CorrelationKey = "future-1",
            CorrelationId = "future-1",
            Text = "future payload",
            PayloadJson = "{\"state\":\"preserved\",\"workspacePath\":\"/private/worktree\",\"memory\":\"internal\"}",
            FirstSeenAt = at,
            LastSeenAt = at,
            RawEventCount = 2,
        };

        var publicResponse = SessionTranscriptBuilder.Build(new AgentSessionTranscriptData([turn], [part]), session);
        Assert.Equal(1, publicResponse.PartCount);
        var publicPart = Assert.Single(Assert.Single(publicResponse.Turns).Assistant);
        Assert.Equal("unknown", publicPart.Type);
        Assert.Equal("unknown", publicPart.Kind);
        Assert.Equal("Unknown runtime event", publicPart.Text);
        Assert.Null(publicPart.Raw);

        var diagnosticResponse = SessionTranscriptBuilder.Build(
            new AgentSessionTranscriptData([turn], [part]), session, "raw");
        var diagnosticPart = Assert.Single(Assert.Single(diagnosticResponse.Turns).Assistant);
        Assert.Equal("unknown", diagnosticPart.Type);
        Assert.Equal("unknown", diagnosticPart.Kind);
        Assert.NotNull(diagnosticPart.Raw);
        Assert.Equal("unknown", diagnosticPart.Raw!.Kind);
        Assert.Equal("future.runtime.event", diagnosticPart.Raw.Type);
        Assert.Equal("future-1", diagnosticPart.Raw.CorrelationKey);
        Assert.Equal("preserved", diagnosticPart.Raw.Payload.GetProperty("state").GetString());
        Assert.False(diagnosticPart.Raw.Payload.TryGetProperty("workspacePath", out _));
        Assert.False(diagnosticPart.Raw.Payload.TryGetProperty("memory", out _));
        Assert.DoesNotContain("/private/worktree", diagnosticPart.Raw.PayloadJson, StringComparison.Ordinal);
        Assert.Equal("{\"state\":\"preserved\"}", diagnosticPart.Raw.PayloadJson);
        Assert.Equal(2, diagnosticPart.Raw.RawEventCount);
    }

    [Fact]
    public void Build_CanonicalInputPartStaysInUserProjection()
    {
        var at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = CreateSession(at);
        var turn = new AgentSessionTranscriptTurnRow
        {
            Id = 1,
            SessionId = session.Id,
            Sequence = 1,
            PromptText = "canonical prompt",
            StartedAt = at,
            UpdatedAt = at,
        };
        var parts = new[]
        {
            new AgentSessionTranscriptPartRow
            {
                Id = 1,
                TurnId = turn.Id,
                Sequence = 1,
                Type = TranscriptPartTypes.Input,
                PayloadJson = "{\"text\":\"canonical prompt\"}",
                FirstSeenAt = at,
                LastSeenAt = at,
            },
            new AgentSessionTranscriptPartRow
            {
                Id = 2,
                TurnId = turn.Id,
                Sequence = 2,
                Type = TranscriptPartTypes.Text,
                Text = "canonical reply",
                PayloadJson = "{\"text\":\"canonical reply\"}",
                FirstSeenAt = at,
                LastSeenAt = at,
            },
        };

        var resultTurn = Assert.Single(SessionTranscriptBuilder.Build(
            new AgentSessionTranscriptData([turn], parts), session).Turns);

        Assert.Equal("canonical prompt", resultTurn.User.Text);
        var reply = Assert.Single(resultTurn.Assistant);
        Assert.Equal("canonical reply", reply.Text);
    }

    [Theory]
    [InlineData(AgentSessionActivity.Unknown, "unknown")]
    [InlineData(AgentSessionActivity.Idle, "completed")]
    public void Build_ProjectsAuthoritativeActivityAndTurnStatus(AgentSessionActivity activity, string expectedStatus)
    {
        var at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = CreateSession(at, workDir: string.Empty);
        session.Status = session.Status with { Activity = activity };
        var turn = new AgentSessionTranscriptTurnRow
        {
            Id = 1,
            SessionId = session.Id,
            Sequence = 1,
            StartedAt = at,
            UpdatedAt = at,
        };

        var response = SessionTranscriptBuilder.Build(new AgentSessionTranscriptData([turn], []), session);

        Assert.Equal(expectedStatus, response.Status);
        Assert.Equal(activity == AgentSessionActivity.Unknown ? "unknown" : activity == AgentSessionActivity.Idle ? "idle" : "active", response.Activity);
    }

    [Fact]
    public void Build_RendersResetAndCompactionEvidenceAfterReload()
    {
        var at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var turn = new AgentSessionTranscriptTurnRow
        {
            Id = 1,
            SessionId = "session-1",
            RuntimeSessionId = "runtime-2",
            Sequence = 1,
            PromptKind = "recovery",
            StartedAt = at,
            UpdatedAt = at,
        };
        var resetPayload = "{\"reason\":\"reset\",\"observedAt\":\"2026-08-01T12:00:00.0000000Z\"}";
        var compactionPayload = "{\"strategy\":\"summary\",\"summary\":\"Earlier context retained\"}";
        var parts = new[]
        {
            new AgentSessionTranscriptPartRow
            {
                Id = 1,
                TurnId = turn.Id,
                Sequence = 1,
                Type = TranscriptPartTypes.SessionContextReset,
                PayloadJson = resetPayload,
                FirstSeenAt = at,
                LastSeenAt = at,
            },
            new AgentSessionTranscriptPartRow
            {
                Id = 2,
                TurnId = turn.Id,
                Sequence = 2,
                Type = TranscriptPartTypes.Compaction,
                PayloadJson = compactionPayload,
                FirstSeenAt = at.AddSeconds(1),
                LastSeenAt = at.AddSeconds(1),
            },
            new AgentSessionTranscriptPartRow
            {
                Id = 3,
                TurnId = turn.Id,
                Sequence = 3,
                Type = "compaction_event",
                PayloadJson = compactionPayload,
                FirstSeenAt = at.AddSeconds(1),
                LastSeenAt = at.AddSeconds(1),
            },
        };

        var response = SessionTranscriptBuilder.Build(new AgentSessionTranscriptData([turn], parts));
        var resultTurn = Assert.Single(response.Turns);

        Assert.Equal("Context reset: reset", resultTurn.User.Text);
        var markers = resultTurn.Assistant.Where(part => part.Type == "error").ToArray();
        Assert.Collection(
            markers,
            reset =>
            {
                Assert.Equal("context-reset", reset.Kind);
                Assert.Contains("new runtime context", reset.Message, StringComparison.Ordinal);
            },
            compaction =>
            {
                Assert.Equal("compaction", compaction.Kind);
                Assert.Contains("Earlier context retained", compaction.Message, StringComparison.Ordinal);
            });
    }

    [Theory]
    [InlineData(AgentTurnStatus.Failed, "failed")]
    [InlineData(AgentTurnStatus.Cancelled, "cancelled")]
    [InlineData(AgentTurnStatus.Completed, "completed")]
    public void Build_MissingCanonicalTerminalTurn_PreservesStatusAndResult(
        AgentTurnStatus status,
        string expectedStatus)
    {
        var at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = CreateSession(at);
        var result = new AgentTurnResult(
            Message: "terminal message",
            Output: "terminal output",
            FailureReason: status == AgentTurnStatus.Failed ? "runner failed" : null,
            FailureCategory: status == AgentTurnStatus.Failed ? "runtime" : null,
            ExitCode: status == AgentTurnStatus.Failed ? 7 : null);
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Idle,
            Inputs = [new AgentSessionInputRecord(
                "input-1", 1, "terminal task", "agent-launch",
                AgentSessionInputAcceptance.Accepted, at, JobId: "job-1")],
            Turns = [new AgentTurnRecord(
                "turn-1", 1, ["input-1"], status, JobId: "job-1", Result: result,
                RecordedAt: at, UpdatedAt: at.AddSeconds(1))],
        };

        var transcript = new AgentSessionTranscriptData([], []);
        var publicResponse = SessionTranscriptBuilder.Build(transcript, session);
        var publicTurn = Assert.Single(publicResponse.Turns);

        Assert.Equal(expectedStatus, publicResponse.Status);
        Assert.Equal(expectedStatus, publicTurn.Status);
        Assert.Equal("terminal task", publicTurn.User.Text);
        Assert.Equal(result.Message, publicTurn.Result?.Message);
        Assert.Equal(result.Output, publicTurn.Result?.Output);
        Assert.Equal(result.FailureReason, publicTurn.Result?.FailureReason);
        Assert.Equal(result.FailureCategory, publicTurn.Result?.FailureCategory);
        Assert.Equal(result.ExitCode, publicTurn.Result?.ExitCode);

        var rawResponse = SessionTranscriptBuilder.Build(transcript, session, "raw");
        var rawTurn = Assert.Single(rawResponse.Turns);
        Assert.Equal(expectedStatus, rawResponse.Status);
        Assert.Equal(expectedStatus, rawTurn.Status);
        Assert.Equal(result.Message, rawTurn.Result?.Message);
        Assert.Equal(result.Output, rawTurn.Result?.Output);
        Assert.Equal(result.FailureReason, rawTurn.Result?.FailureReason);
        Assert.Equal(result.FailureCategory, rawTurn.Result?.FailureCategory);
        Assert.Equal(result.ExitCode, rawTurn.Result?.ExitCode);
    }
}
