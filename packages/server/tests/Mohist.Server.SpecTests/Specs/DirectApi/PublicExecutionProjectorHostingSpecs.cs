using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

/// <summary>
/// The hosted public execution projector wiring: the projector runs as
/// the only writer behind a nudge channel plus a timer sweep, and the
/// nudge raised by a canonical write path is enough for the projection
/// to catch up from the checkpoint — without any caller involvement.
/// </summary>
[Collection("WorkflowRuntimeIntegration")]
public sealed class PublicExecutionProjectorHostingSpecs(IsolatedMohistIntegrationFixture fixture)
{
    [Fact]
    public async Task NudgedHostedProjector_CatchesUpFromTheCheckpoint()
    {
        var sessionId = $"session-hosted-{Guid.NewGuid():N}";
        var inputId = $"input-hosted-{Guid.NewGuid():N}";
        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
            var session = AgentSession.Create(
                sessionId,
                "runner-1",
                "/mohist-tests/work",
                new AgentSessionMetadata(Labels: new Dictionary<string, string>
                {
                    ["mohist.io/project-id"] = "proj_pub",
                    ["mohist.io/source-kind"] = "agent-launch",
                    ["mohist.io/agent-id"] = "agent_pub",
                }),
                now: now);
            session.Status = session.Status with
            {
                Activity = AgentSessionActivity.Active,
                Inputs = [new AgentSessionInputRecord(
                    Id: inputId,
                    Sequence: 1,
                    Text: "Investigate the failed deployment",
                    Source: "direct-test",
                    Acceptance: AgentSessionInputAcceptance.Accepted,
                    RecordedAt: now,
                    JobId: null)],
            };
            await store.SaveAsync(session.Id, session);
        }

        await fixture.Services.GetRequiredService<PublicProjectionNudge>().NudgeAndWaitAsync();

        await using var projectionScope = fixture.Services.CreateAsyncScope();
        var projectionDb = projectionScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var snapshot = await projectionDb.PublicExecutionSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(row => row.AnchorType == "input" && row.AnchorId == inputId);

        Assert.NotNull(snapshot);
        Assert.Contains("\"status\":\"accepted\"", snapshot!.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"inputStatus\":\"accepted\"", snapshot.SnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Investigate the failed deployment", snapshot.SnapshotJson, StringComparison.Ordinal);

        // The projector also published the matching public event beside
        // the snapshot in the same checkpointed stream.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var events = await db.PublicSessionEvents.AsNoTracking()
                .Where(row => row.SessionId == sessionId)
                .OrderBy(row => row.Sequence)
                .ToListAsync();
            var accepted = Assert.Single(events, row => row.Type == "input.accepted");
            Assert.Equal(1, accepted.Sequence);
            var stream = await db.PublicStreamStates.AsNoTracking()
                .SingleAsync(row => row.SessionId == sessionId);
            Assert.Equal(1, stream.ActiveGeneration);
            Assert.Equal(1, stream.LatestSequence);
        }
    }
}
