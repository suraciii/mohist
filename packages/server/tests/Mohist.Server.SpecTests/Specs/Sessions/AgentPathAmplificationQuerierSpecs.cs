using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Calculation specs for the path-amplification projection exercised by
/// the activity and status routes (<c>amplification.candidates /
/// processed / transcriptRecords / databaseCalls / downstreamCalls</c>).
/// The projection counts only Sessions that can contribute to the
/// active-agent readout, not the full project Session history, and the
/// transcript-records counter reflects only the preview materialization
/// (no duplicate summary pass). Specs drive
/// <see cref="AgentActivityFeedAssembler.GetActivityAsync"/> directly
/// via <c>MohistDbFixture</c> (no web host, no HTTP). The route
/// contract (404 / 400 alias selectors, parity of alias vs canonical)
/// stays in <c>AgentPathAmplificationSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class AgentPathAmplificationQuerierSpecs
{
    private static readonly string[] AmplificationFields =
        ["candidates", "databaseCalls", "downstreamCalls", "processed", "transcriptRecords"];

    private readonly MohistDbFixture _fixture;

    public AgentPathAmplificationQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Status_ReportsFilteredCandidatesWithTruthfulCounts()
    {
        var projectId = $"proj-status-filter-{Guid.NewGuid():N}";
        await InsertSessionsAsync(projectId, count: 1, activeCount: 1);

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var small = await assembler.GetActivityAsync(projectId);
        await InsertSessionsAsync(projectId, count: 20, activeCount: 0);

        var activity = await assembler.GetActivityAsync(projectId);
        var amplification = activity.Amplification;

        AssertAmplificationShape(amplification);
        Assert.Equal(small.Amplification.Candidates, amplification.Candidates);
        Assert.Equal(1, amplification.Processed);
        Assert.Equal(0, amplification.TranscriptRecords);
        Assert.Equal(small.Amplification.DatabaseCalls, amplification.DatabaseCalls);
        Assert.Equal(small.Amplification.DownstreamCalls, amplification.DownstreamCalls);
    }

    [Fact]
    public async Task Status_WithoutCurrentAgents_KeepsExplicitAmplification()
    {
        var projectId = $"proj-status-empty-{Guid.NewGuid():N}";

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var activity = await assembler.GetActivityAsync(projectId);
        var amplification = activity.Amplification;

        AssertAmplificationShape(amplification);
        Assert.Equal(0, amplification.Candidates);
        Assert.Equal(0, amplification.Processed);
        Assert.Equal(0, amplification.TranscriptRecords);
    }

    [Fact]
    public async Task Activity_CountsOnlyPreviewTranscriptMaterialization()
    {
        var projectId = $"proj-activity-transcript-{Guid.NewGuid():N}";
        var sessionIds = await InsertSessionsAsync(projectId, count: 1, activeCount: 1);
        await InsertTranscriptPartsAsync(sessionIds[0], 2);

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var activity = await assembler.GetActivityAsync(projectId);
        var amplification = activity.Amplification;

        AssertAmplificationShape(amplification);
        Assert.Equal(1, amplification.Candidates);
        Assert.Equal(1, amplification.Processed);
        Assert.Equal(2, amplification.TranscriptRecords);
        Assert.True(amplification.DatabaseCalls > 0);
        Assert.True(amplification.DownstreamCalls > 0);
    }

    [Fact]
    public async Task Activity_LimitsCandidatesAndCardsToTwoHundred()
    {
        var projectId = $"proj-activity-limit-{Guid.NewGuid():N}";
        await InsertSessionsAsync(projectId, count: 1, activeCount: 1);

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var small = await assembler.GetActivityAsync(projectId, limit: 10_000);
        await InsertSessionsAsync(projectId, count: 204, activeCount: 204);

        var activity = await assembler.GetActivityAsync(projectId, limit: 10_000);

        Assert.Equal(200, activity.Sessions.Count);
        Assert.Equal(200, activity.Amplification.Candidates);
        Assert.Equal(200, activity.Amplification.Processed);
        Assert.Equal(small.Amplification.DatabaseCalls, activity.Amplification.DatabaseCalls);
        Assert.Equal(small.Amplification.DownstreamCalls, activity.Amplification.DownstreamCalls);
        AssertAmplificationShape(activity.Amplification);
    }

    [Fact]
    public async Task Status_ActiveAgentsCount_ReflectsFilteredTruthfulCount()
    {
        var projectId = $"proj-status-truthful-{Guid.NewGuid():N}";
        await InsertSessionsAsync(projectId, count: 5, activeCount: 2);
        await InsertSessionsAsync(projectId, count: 10, activeCount: 0);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<Mohist.Server.Workflow.Services.WorkflowActivityQuerier>();

        var result = await querier.ListActiveAgentsResultAsync(projectId);

        Assert.Equal(2, result.ActiveAgents.Count);
        Assert.True(result.Candidates >= 5);
    }

    private static void AssertAmplificationShape(AgentAmplificationDto amplification)
    {
        var props = typeof(AgentAmplificationDto).GetProperties();
        Assert.Equal(AmplificationFields, props.Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]).Order(StringComparer.Ordinal));
    }

    private async Task<IReadOnlyList<string>> InsertSessionsAsync(string projectId, int count, int activeCount)
    {
        var now = TestTime.UtcDateTime;
        var ids = Enumerable.Range(0, count).Select(_ => $"session-{Guid.NewGuid():N}").ToArray();
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();

        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            var session = new AgentSession
            {
                Id = id,
                Runtime = new AgentSessionRuntime("runner-amplification", null),
                Settings = new AgentSessionSettings("test-model"),
                Status = new AgentSessionStatusSnapshot(
                    CreatedAt: now,
                    BoundAt: now,
                    LastDataAt: index < activeCount ? now : null,
                    AgentRuntimeSessionId: id,
                    Activity: index < activeCount ? AgentSessionActivity.Active : AgentSessionActivity.Idle),
                Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = $"agent-{index}",
                    [GenericAgentSessionMetadata.AgentName] = $"Agent {index}",
                }),
            };
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = id,
                State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
                CreatedAt = now.AddTicks(index),
                Status = "bound",
                AgentSessionId = id,
                RunnerId = "runner-amplification",
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task InsertTranscriptPartsAsync(string sessionId, int count)
    {
        var now = TestTime.UtcDateTime;
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            Sequence = 1,
            StartedAt = now,
            UpdatedAt = now,
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        for (var index = 0; index < count; index++)
        {
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = index + 1,
                Type = "message.delta",
                CorrelationKey = $"part-{Guid.NewGuid():N}",
                PayloadJson = $"{{\"text\":\"message-{index}\"}}",
                LastSeenAt = now.AddTicks(index),
            });
        }

        await db.SaveChangesAsync();
    }
}