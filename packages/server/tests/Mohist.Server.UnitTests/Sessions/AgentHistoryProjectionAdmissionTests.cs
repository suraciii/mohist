using System.Reflection;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class AgentHistoryProjectionAdmissionTests
{
    [Fact]
    public void SessionUsageSummary_is_not_turn_attributed()
    {
        var session = AgentSession.Create(
            id: "session-history",
            runnerId: "runner-history",
            workDir: "/tmp/internal",
            metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", "project-history")
                .WithLabel("mohist.io/source-kind", "agent-launch")
                .WithLabel("mohist.io/agent-id", "agent-history"),
            now: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc));
        session.Status = session.Status with
        {
            UsageSummary = new AgentUsageSummary(
                InputTokens: 100,
                OutputTokens: 40,
                TotalTokens: 140,
                CostAmount: 1.25,
                CostCurrency: "USD"),
            Turns =
            [
                new AgentTurnRecord(
                    "turn-1",
                    1,
                    ["input-1"],
                    AgentTurnStatus.Completed,
                    RecordedAt: session.Status.CreatedAt,
                    UpdatedAt: session.Status.CreatedAt.AddSeconds(1)),
                new AgentTurnRecord(
                    "turn-2",
                    2,
                    ["input-2"],
                    AgentTurnStatus.Completed,
                    RecordedAt: session.Status.CreatedAt.AddSeconds(2),
                    UpdatedAt: session.Status.CreatedAt.AddSeconds(3)),
            ]
        };

        Assert.NotNull(session.Status.UsageSummary);
        Assert.Equal(2, session.Status.Turns!.Count);
        Assert.DoesNotContain(
            typeof(AgentTurnRecord).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name.Contains("Usage", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Cost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TranscriptUsagePart_has_no_canonical_turn_usage_revision()
    {
        var part = new AgentSessionTranscriptPartRow
        {
            TurnId = 42,
            Type = "usage",
            CorrelationKey = "usage",
            PayloadJson = "{\"inputTokens\":100,\"outputTokens\":40,\"costAmount\":1.25}",
            FirstSeenAt = new DateTime(2026, 8, 15, 0, 0, 1, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 8, 15, 0, 0, 1, DateTimeKind.Utc),
            RawEventCount = 1,
        };

        Assert.Equal("usage", part.CorrelationKey);
        Assert.DoesNotContain("turnId", part.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("revision", part.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(
            typeof(long),
            typeof(AgentSessionTranscriptPartRow).GetProperty(nameof(AgentSessionTranscriptPartRow.TurnId))!.PropertyType);
        Assert.Null(typeof(AgentSessionTranscriptPartRow).GetProperty("SourceRevision"));
    }
}
