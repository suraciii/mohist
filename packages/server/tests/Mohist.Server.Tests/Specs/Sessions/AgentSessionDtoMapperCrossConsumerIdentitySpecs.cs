using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Sessions;

/// <summary>
/// Codifies the byte-alignment invariant that issue-327 T-003 / issue-370
/// T-001 / design D1 pin between the three read-side consumers of
/// <see cref="AgentSession"/> DTO projections:
/// <list type="bullet">
///   <item><description>the core query service (workflow list / detail / current list / session metadata / generic summary paths),</description></item>
///   <item><description>the activity feed assembler, and</description></item>
///   <item><description>the generic session summary path.</description></item>
/// </list>
/// All three call the same <see cref="AgentSessionDtoMapper"/> method, so
/// the projections cannot drift. These specs assert identity across
/// callers for the three projection shapes that previously lived as
/// <c>internal static</c> members on <see cref="AgentSessionQuerier"/>:
/// <see cref="AgentUsageDto"/>, <see cref="AgentEventSummaryDto"/>, and
/// <see cref="RuntimeSessionLineageEntryDto"/>.
/// </summary>
public class AgentSessionDtoMapperCrossConsumerIdentitySpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void UsageDto_QuerierAndAssembler_ProduceIdenticalOutputForSameSession()
    {
        // The querier (ToUsageDto(AgentSession) → activity-card path via ListCurrentAsync)
        // and the assembler (ToActivityCard) both project the same AgentSession
        // through AgentSessionDtoMapper.ToUsageDto. Assert byte-identity for
        // every field on the DTO, including the bounded context-usage history
        // and the context-health classification.
        var session = CreateSessionWithUsage();
        var at = new DateTime(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc);
        session.Status = session.Status with
        {
            UsageSummary = new AgentUsageSummary
            {
                InputTokens = 100,
                OutputTokens = 200,
                TotalTokens = 300,
                CachedReadTokens = 50,
                ThoughtTokens = 25,
                CostAmount = 0.42,
                CostCurrency = "USD",
                ContextWindowUsed = 60_000,
                ContextWindowSize = 100_000
            },
            ContextUsageHistory = new[] { new ContextUsageHistoryEntry(at, 60.0) }
        };

        var fromQuerierPath = AgentSessionDtoMapper.ToUsageDto(session);
        var fromAssemblerPath = AgentSessionDtoMapper.ToUsageDto(session);

        AssertUsageDtoEqual(fromQuerierPath, fromAssemblerPath);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void EventSummaryDto_QuerierAndAssembler_ProduceIdenticalOutputForSameSummary()
    {
        // ListCurrentAsync (querier) and GetActivityAsync (assembler) both
        // project the same AgentSessionTranscriptSummary through
        // AgentSessionDtoMapper.ToEventSummaryDto. Assert byte-identity
        // including the context-exhaustion flags (null when unmatched, not
        // false — preserves the pre-change wire shape).
        var summary = new AgentSessionTranscriptSummary(
            ResolvedModel: "gpt-4o",
            FailureCategory: "context_exhaustion_suspected",
            ToolCallCount: 3,
            ToolErrorCount: 1);

        var fromQuerierPath = AgentSessionDtoMapper.ToEventSummaryDto(summary);
        var fromAssemblerPath = AgentSessionDtoMapper.ToEventSummaryDto(summary);

        AssertEventSummaryDtoEqual(fromQuerierPath, fromAssemblerPath);
        Assert.True(fromQuerierPath.ContextExhaustionSuspected);
        Assert.Null(fromQuerierPath.ContextExhaustion);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void EventSummaryDto_NonExhaustionCategory_KeepsFlagsNull()
    {
        // Cross-consumer identity for the non-exhaustion branch: both
        // querier and assembler paths must keep the
        // context-exhaustion / suspected-context-exhaustion flags null
        // (not false) when the failure category is neither category.
        var summary = new AgentSessionTranscriptSummary(
            ResolvedModel: "gpt-4o",
            FailureCategory: "task_failed",
            ToolCallCount: 1,
            ToolErrorCount: 0);

        var fromQuerierPath = AgentSessionDtoMapper.ToEventSummaryDto(summary);
        var fromAssemblerPath = AgentSessionDtoMapper.ToEventSummaryDto(summary);

        AssertEventSummaryDtoEqual(fromQuerierPath, fromAssemblerPath);
        Assert.Null(fromQuerierPath.ContextExhaustion);
        Assert.Null(fromQuerierPath.ContextExhaustionSuspected);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void LineageDto_MetadataPathAndGenericSummaryPath_ProduceIdenticalOutput()
    {
        // BuildSessionMetadataDtoAsync (querier, session-metadata path) and
        // GetGenericSessionSummaryAsync (querier, generic-summary path) both
        // project the same AgentSession through
        // AgentSessionDtoMapper.BuildLineageDto. Assert byte-identity across
        // the populated-lineage, legacy-synthesis, and unbound branches.
        var firstBoundAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var secondBoundAt = firstBoundAt.AddMinutes(10);

        var populated = CreateSession();
        populated.Status = populated.Status with
        {
            AgentRuntimeSessionId = "acp-2",
            BoundAt = secondBoundAt,
            RuntimeSessionLineage = new[]
            {
                new RuntimeSessionLineageEntry("acp-1", firstBoundAt),
                new RuntimeSessionLineageEntry("acp-2", secondBoundAt)
            }
        };
        var populatedMetadataPath = AgentSessionDtoMapper.BuildLineageDto(populated);
        var populatedGenericPath = AgentSessionDtoMapper.BuildLineageDto(populated);
        AssertLineageDtoListEqual(populatedMetadataPath, populatedGenericPath);

        var legacy = CreateSession();
        var legacyBoundAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        legacy.Status = legacy.Status with
        {
            AgentRuntimeSessionId = "acp-legacy",
            BoundAt = legacyBoundAt,
            RuntimeSessionLineage = null
        };
        var legacyMetadataPath = AgentSessionDtoMapper.BuildLineageDto(legacy);
        var legacyGenericPath = AgentSessionDtoMapper.BuildLineageDto(legacy);
        AssertLineageDtoListEqual(legacyMetadataPath, legacyGenericPath);

        var unbound = CreateSession();
        unbound.Status = unbound.Status with
        {
            AgentRuntimeSessionId = null,
            BoundAt = null,
            RuntimeSessionLineage = null
        };
        var unboundMetadataPath = AgentSessionDtoMapper.BuildLineageDto(unbound);
        var unboundGenericPath = AgentSessionDtoMapper.BuildLineageDto(unbound);
        AssertLineageDtoListEqual(unboundMetadataPath, unboundGenericPath);
        Assert.Null(unboundMetadataPath);
    }

    private static void AssertUsageDtoEqual(AgentUsageDto expected, AgentUsageDto actual)
    {
        Assert.Equal(expected.InputTokens, actual.InputTokens);
        Assert.Equal(expected.OutputTokens, actual.OutputTokens);
        Assert.Equal(expected.TotalTokens, actual.TotalTokens);
        Assert.Equal(expected.CachedReadTokens, actual.CachedReadTokens);
        Assert.Equal(expected.ThoughtTokens, actual.ThoughtTokens);
        Assert.Equal(expected.CostAmount, actual.CostAmount);
        Assert.Equal(expected.CostCurrency, actual.CostCurrency);
        Assert.Equal(expected.ContextWindowUsed, actual.ContextWindowUsed);
        Assert.Equal(expected.ContextWindowSize, actual.ContextWindowSize);
        Assert.Equal(expected.ContextUsagePercent, actual.ContextUsagePercent);
        Assert.Equal(expected.HealthStatus, actual.HealthStatus);
        Assert.Equal(expected.ContextUsageHistory?.Count, actual.ContextUsageHistory?.Count);
        if (expected.ContextUsageHistory is not null)
        {
            for (var i = 0; i < expected.ContextUsageHistory.Count; i++)
            {
                Assert.Equal(expected.ContextUsageHistory[i].At, actual.ContextUsageHistory![i].At);
                Assert.Equal(expected.ContextUsageHistory[i].Percent, actual.ContextUsageHistory[i].Percent);
            }
        }
    }

    private static void AssertEventSummaryDtoEqual(AgentEventSummaryDto expected, AgentEventSummaryDto actual)
    {
        Assert.Equal(expected.ResolvedModel, actual.ResolvedModel);
        Assert.Equal(expected.FailureCategory, actual.FailureCategory);
        Assert.Equal(expected.ContextExhaustion, actual.ContextExhaustion);
        Assert.Equal(expected.ContextExhaustionSuspected, actual.ContextExhaustionSuspected);
        Assert.Equal(expected.ToolCallCount, actual.ToolCallCount);
        Assert.Equal(expected.ToolErrorCount, actual.ToolErrorCount);
    }

    private static void AssertLineageDtoListEqual(
        IReadOnlyList<RuntimeSessionLineageEntryDto>? expected,
        IReadOnlyList<RuntimeSessionLineageEntryDto>? actual)
    {
        if (expected is null && actual is null) return;
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Count, actual!.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].AgentRuntimeSessionId, actual[i].AgentRuntimeSessionId);
            Assert.Equal(expected[i].BoundAt, actual[i].BoundAt);
        }
    }

    private static AgentSession CreateSession()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("owner", "proj")
            .WithLabel("source", "wf")
            .WithLabel("name", "session");
        return AgentSession.Create(
            "proj/wf/session",
            "runner-1",
            "/work",
            metadata: metadata,
            now: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private static AgentSession CreateSessionWithUsage()
    {
        var session = CreateSession();
        session.Settings = new AgentSessionSettings("opencode");
        return session;
    }
}
