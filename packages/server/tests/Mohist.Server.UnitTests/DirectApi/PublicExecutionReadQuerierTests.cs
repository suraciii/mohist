using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.DirectApi;

/// <summary>
/// Verifies the shared projection read service distinguishes an owned
/// canonical anchor from a missing resource while its snapshot is still
/// waiting for the projector.
/// </summary>
public sealed class PublicExecutionReadQuerierTests
{
    [Fact]
    public async Task OwnedCanonicalInputAndTurnWithoutSnapshots_ReturnProjectionLagThenRecover()
    {
        await using var support = new PublicProjectionTestSupport();
        var projectId = "direct-read-lag-project";
        var sessionId = "direct-read-lag-session";
        var inputId = "direct-read-lag-input";
        var turnId = "direct-read-lag-turn";
        var session = support.BuildSession(sessionId, projectId, "agent-public");
        var input = PublicProjectionTestSupport.Input(inputId, jobId: null);
        var turn = PublicProjectionTestSupport.Turn(
            turnId,
            inputId,
            jobId: null,
            AgentTurnStatus.Executing);
        PublicProjectionTestSupport.WithFacts(
            session,
            AgentSessionActivity.Active,
            [input],
            [turn]);
        await support.SaveSessionAsync(session);

        var querier = new PublicExecutionReadQuerier(support.DbFactory);
        var beforeInput = await querier.ReadInputAsync(projectId, inputId);
        var beforeTurn = await querier.ReadTurnAsync(projectId, turnId);

        Assert.Equal(PublicReadStatus.ProjectionLag, beforeInput.Status);
        Assert.Equal(PublicReadStatus.ProjectionLag, beforeTurn.Status);
        Assert.Null(await support.SnapshotAsync("input", inputId));
        Assert.Null(await support.SnapshotAsync("turn", turnId));

        Assert.True(await support.Engine.ProcessPendingAsync());

        var afterInput = await querier.ReadInputAsync(projectId, inputId);
        var afterTurn = await querier.ReadTurnAsync(projectId, turnId);
        Assert.Equal(PublicReadStatus.Found, afterInput.Status);
        Assert.Equal(PublicReadStatus.Found, afterTurn.Status);
        Assert.Contains(inputId, afterInput.SnapshotJson!, StringComparison.Ordinal);
        Assert.Contains(turnId, afterTurn.SnapshotJson!, StringComparison.Ordinal);
    }
}
