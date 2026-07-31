using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackTerminalDeliveryHandlerSpecs
{
    [Theory]
    [InlineData("completed", SlackOutboxKinds.TerminalResult, "The task completed.", "Review the evidence")]
    [InlineData("failed", SlackOutboxKinds.ExplicitFailure, "The task failed.", "Reply with corrected instructions")]
    [InlineData("unknown", SlackOutboxKinds.ExplicitFailure, "The task outcome is unknown.", "Wait for reconciliation")]
    public async Task HandleAsync_RendersTerminalOutcomeAndEnqueuesIdempotently(
        string status,
        string expectedKind,
        string expectedConclusion,
        string expectedNextStep)
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        await using (var db = database.CreateContext())
        {
            db.AgentConnections.Add(new AgentConnectionRow
            {
                Id = "conn-1",
                ProjectId = "proj-1",
                AgentId = "agent-1",
                ProviderKind = ConnectionProviderKind.Slack,
                WorkspaceTeamId = "team-1",
                AppId = "app-1",
                BotUserId = "bot-1",
                BotName = "Mohist",
                SetupProgress = SetupProgressKind.Complete,
                DesiredState = DesiredStateKind.Enabled,
                ConnectionHealth = ConnectionHealthKind.Healthy,
                AgentReadiness = AgentReadinessKind.Ready,
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped<SlackOutboxStore>(_ => new SlackOutboxStore(
            new TestDbContextFactory(database.Options),
            new NoopHealthBackpressurer(),
            time,
            Options.Create(new SlackProviderOptions { OutboxCapacityPerConnection = 1 })));
        await using var provider = services.BuildServiceProvider();
        var handler = new SlackTerminalDeliveryHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlackTerminalDeliveryHandler>.Instance);
        var delivery = new
        {
            jobKey = "job-1",
            workLabel = "ship the release",
            connectionId = "conn-1",
            workspaceTeamId = "team-1",
            dmConversationId = "D1",
            status,
            message = "completed with token=xoxb-secret",
            output = "raw tool output: internal command log",
            failureReason = "failure details",
            failureCategory = "runtime-failed",
            artifactCount = 2,
            exitCode = 1,
        };
        var evt = new CloudEvent(
            "delivery-1",
            new Uri("/mohist/agent-job/job-1", UriKind.Relative),
            EventCatalog.ReverseDns.AgentJobTerminalDelivery,
            time.GetUtcNow(),
            JsonSerializer.SerializeToElement(delivery),
            subject: "job-1",
            extensions: new Dictionary<string, string> { [EventCatalog.Lineage.ProjectId] = "proj-1" });

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var rows = (await scope.ServiceProvider.GetRequiredService<SlackOutboxStore>().ListAsync("proj-1", "conn-1")).Entries;
        var row = Assert.Single(rows);
        Assert.Equal(expectedKind, row.Kind);
        var text = JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("text").GetString()!;
        Assert.StartsWith("Task: ship the release\n", text, StringComparison.Ordinal);
        Assert.Contains($"Conclusion: {expectedConclusion}", text);
        Assert.Contains("Evidence: ", text);
        Assert.True(text.Contains($"Next step: {expectedNextStep}", StringComparison.Ordinal), text);
        Assert.DoesNotContain("xoxb-secret", text);
        Assert.DoesNotContain("raw tool output", text);
    }

    [Fact]
    public void BuildTerminalDeliveryEnvelope_CarriesTheFirstSessionInputAsAnEightyCharacterLabel()
    {
        var prompt = "Investigate the release pipeline and document every failing check before proposing a fix. "
            + "Then summarize the safest rollout.";
        var pending = new PendingTerminalDeliveryEvent(
            EventId: "delivery-1",
            Origin: new ConnectionLaunchOrigin("conn-1", "team-1", "U_OWNER", "D1", "1710000000.000001"),
            Status: AgentJobStatus.Completed,
            Message: "completed",
            FailureReason: null,
            FailureCategory: null,
            ArtifactCount: 0,
            ExitCode: 0,
            RecordedAt: new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        var envelope = AgentJobLineage.BuildTerminalDeliveryEnvelope(
            "job-1",
            pending,
            new Dictionary<string, string> { [EventCatalog.Lineage.ProjectId] = "proj-1" },
            prompt);

        var workLabel = envelope.Data!.Value.GetProperty("workLabel").GetString();
        Assert.Equal(prompt[..80], workLabel);
    }

    [Fact]
    public void Render_AlwaysKeepsCompletedAndFailedRepliesTiedToTheirOriginatingWork()
    {
        var priorWork = new SlackTerminalDelivery(
            "job-prior",
            "migrate the database schema",
            "conn-1",
            "team-1",
            "D1",
            "failed",
            "migration failed",
            "migration-error",
            "runtime-failed",
            0,
            1);
        var currentWork = new SlackTerminalDelivery(
            "job-current",
            "update the dashboard",
            "conn-1",
            "team-1",
            "D1",
            "completed",
            "dashboard updated",
            null,
            null,
            0,
            0);

        var priorReply = SlackTerminalDeliveryHandler.Render(priorWork);
        var currentReply = SlackTerminalDeliveryHandler.Render(currentWork);

        Assert.StartsWith("Task: migrate the database schema\n", priorReply, StringComparison.Ordinal);
        Assert.Contains("The task failed.", priorReply);
        Assert.StartsWith("Task: update the dashboard\n", currentReply, StringComparison.Ordinal);
        Assert.Contains("The task completed.", currentReply);
        Assert.DoesNotContain("update the dashboard", priorReply);
        Assert.DoesNotContain("migrate the database schema", currentReply);
    }

    private sealed class NoopHealthBackpressurer : ISlackConnectionHealthBackpressurer
    {
        public Task FlipBackpressuredAsync(string projectId, string connectionId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
