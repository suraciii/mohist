using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workspace.Grains;
using Mohist.Server.Workspace.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed partial class SlackChannelThreadIngressSpecs
{
    [Fact]
    public async Task Accepted_ingress_clears_offline_gap_after_reconnect()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = $"slack-gap-clear-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            $"{runnerId}-host",
            connection.ProjectId));
        await runner.UpdateAsync(1);
        var gapAt = _fixture.TimeProvider.GetUtcNow();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.AgentConnections
                .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.OfflineGapAt, gapAt));
            var workspaceName = await scope.ServiceProvider
                .GetRequiredService<InteractionWorkspaceProvisioner>()
                .EnsureSlackWorkspaceAsync(
                    connection.ProjectId,
                    connection.WorkspaceTeamId,
                    "D-gap-clear",
                    gapAt);
            await _fixture.Grains
                .GetGrain<IWorkspaceGrain>(GrainKey.Workspace(connection.ProjectId, workspaceName))
                .EnsureMaterializedOnAsync(runnerId, $"/tmp/{workspaceName}", gapAt);
        }

        string? jobKey = null;
        try
        {
            using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
            {
                isDirectMessage = true,
                teamId = connection.WorkspaceTeamId,
                conversationId = "D-gap-clear",
                messageTs = "1710000000.000600",
                threadTs = (string?)null,
                mentionedUserIds = Array.Empty<string>(),
                senderSlackUserId = "U_OWNER",
                senderKind = "human",
                text = "first message after reconnect",
                leaseId = _connectionLeases[connection.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            jobKey = payload.RootElement.GetProperty("data").GetProperty("jobKey").GetString();
            Assert.False(string.IsNullOrWhiteSpace(jobKey));

            await using var verify = _fixture.Services.CreateAsyncScope();
            var verifyDb = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
            var reloaded = await verifyDb.AgentConnections.AsNoTracking()
                .SingleAsync(row => row.Id == connection.Id);
            Assert.Null(reloaded.OfflineGapAt);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(jobKey))
            {
                await _fixture.AgentJobDispatches.WaitForAssignmentPreparedAsync(
                    jobKey,
                    TimeSpan.FromSeconds(5));
                var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
                var assignment = await job.GetRuntimeSnapshotAsync();
                Assert.Equal(runnerId, assignment.RunnerId);
                var claim = await runner.TryClaimAgentJobAsync(jobKey, connection.ProjectId);
                Assert.NotNull(claim);
                var report = await job.ReportResultAsync(
                    runnerId,
                    claim.WorkId,
                    new WorkResult(
                        Status: "completed",
                        Message: "test cleanup",
                        Output: JSON.DeserializeElement("{}"),
                        ArtifactUploadIds: null,
                        ExitCode: 0));
                Assert.True(report.Accepted, "AgentJob rejected completed cleanup report");
            }

            await runner.UnregisterAsync();
        }
    }
}
