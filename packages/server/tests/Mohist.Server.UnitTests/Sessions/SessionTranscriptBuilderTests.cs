using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class SessionTranscriptBuilderTests
{
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
}
