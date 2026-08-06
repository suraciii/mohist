using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Project.Services;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackTerminalDeliveryHandlerSpecs
{
    [Theory]
    [InlineData("completed", SlackOutboxKinds.TerminalResult, "The task completed.", "Review the evidence")]
    [InlineData("failed", SlackOutboxKinds.ExplicitFailure, "The task failed.", "Reply with corrected instructions")]
    [InlineData("cancelled", SlackOutboxKinds.ExplicitFailure, "The task was cancelled.", "Send a new request")]
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
            db.Projects.Add(new ProjectRow
            {
                Id = "proj-1",
                Name = "demo",
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            });
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
            db.AgentJobs.Add(new AgentJobRow { JobKey = "job-1", AgentSessionId = "session-1" });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        var slackOptions = Options.Create(new SlackProviderOptions
        {
            OutboxCapacityPerConnection = 1,
            ExternalWebUrl = "https://mohist.example",
        });
        services.AddScoped<SlackOutboxStore>(_ => new SlackOutboxStore(
            new TestDbContextFactory(database.Options),
            new NoopHealthBackpressurer(),
            time,
            slackOptions));
        services.AddScoped<IAgentJobStore>(_ => new AgentJobStore(
            new TestDbContextFactory(database.Options),
            NullLogger<AgentJobStore>.Instance,
            time));
        services.AddSingleton(new ProjectQuerier(new TestDbContextFactory(database.Options)));
        services.AddScoped(_ => new SlackWebLinkBuilder(slackOptions));
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
            conversationId = "D1",
            threadTs = "1710000000.000001",
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
        Assert.Equal("D1", row.ConversationId);
        Assert.Equal("1710000000.000001", row.ThreadTs);
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        var text = payload.Text!;
        Assert.StartsWith("Task: ship the release\n", text, StringComparison.Ordinal);
        Assert.Contains($"Conclusion: {expectedConclusion}", text);
        Assert.Contains("Evidence: ", text);
        Assert.True(text.Contains($"Next step: {expectedNextStep}", StringComparison.Ordinal), text);
        Assert.DoesNotContain("xoxb-secret", text);
        Assert.DoesNotContain("raw tool output", text);
        Assert.Contains("Session: session-1", text);
        Assert.True(payload.Blocks.HasValue);
        var button = payload.Blocks.Value[0].GetProperty("elements")[0];
        Assert.Equal("https://mohist.example/demo/sessions/session-1", button.GetProperty("url").GetString());
    }

    [Fact]
    public async Task HandleAsync_UsesTheAssistantReportAsTheTerminalReply_WhenTheAgentHasOne()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        await using (var db = database.CreateContext())
        {
            db.Projects.Add(new ProjectRow
            {
                Id = "proj-1",
                Name = "demo",
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            });
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
            db.AgentJobs.Add(new AgentJobRow { JobKey = "job-1", AgentSessionId = "session-1" });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped<SlackOutboxStore>(_ => new SlackOutboxStore(
            new TestDbContextFactory(database.Options),
            new NoopHealthBackpressurer(),
            time,
            Options.Create(new SlackProviderOptions { OutboxCapacityPerConnection = 1 })));
        services.AddScoped<IAgentJobStore>(_ => new AgentJobStore(
            new TestDbContextFactory(database.Options),
            NullLogger<AgentJobStore>.Instance,
            time));
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
            conversationId = "D1",
            threadTs = "1710000000.000001",
            status = "completed",
            message = "completed",
            assistantText = "磁盘占用 62%，CPU 平均 18%，内存剩余 3.2G。无需处理。",
            artifactCount = 0,
            exitCode = 0,
        };
        var evt = new CloudEvent(
            "delivery-2",
            new Uri("/mohist/agent-job/job-1", UriKind.Relative),
            EventCatalog.ReverseDns.AgentJobTerminalDelivery,
            time.GetUtcNow(),
            JsonSerializer.SerializeToElement(delivery),
            subject: "job-1",
            extensions: new Dictionary<string, string> { [EventCatalog.Lineage.ProjectId] = "proj-1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var rows = (await scope.ServiceProvider.GetRequiredService<SlackOutboxStore>().ListAsync("proj-1", "conn-1")).Entries;
        var row = Assert.Single(rows);
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Equal(
            "磁盘占用 62%，CPU 平均 18%，内存剩余 3.2G。无需处理。\nSession: session-1",
            payload.Text);
        Assert.DoesNotContain("Task: ship the release", payload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Conclusion:", payload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Next step:", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_Neutralizes_control_syntax_in_a_completed_reply()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        const string enrollmentId = "manager-enrollment-reply";
        await using (var db = database.CreateContext())
        {
            db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
            {
                Id = enrollmentId,
                WorkspaceTeamId = "T_MANAGER_REPLY",
                Lifecycle = SlackEnrollmentLifecycle.Active,
                ManagerCapability = SlackManagerCapability.Available,
                ManagerAppId = "A_MANAGER_REPLY",
                ManagerBotUserId = "U_MANAGER_REPLY",
                ManagerCredentialRef = "manager-credential-reply",
                ManagerReadiness = SlackManagerReadiness.Ready,
                ManagerActorId = "manager-actor-reply",
                PlanCode = "unknown",
                AuditJson = "[]",
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
            Options.Create(new SlackProviderOptions { OutboxCapacityPerConnection = 4 })));
        await using var provider = services.BuildServiceProvider();
        var handler = new SlackTerminalDeliveryHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlackTerminalDeliveryHandler>.Instance);
        var delivery = new
        {
            jobKey = "manager-job-reply",
            workLabel = "answer the manager",
            connectionId = enrollmentId,
            workspaceTeamId = "T_MANAGER_REPLY",
            conversationId = "D_MANAGER_REPLY",
            messageTs = "1710000000.000001",
            status = "completed",
            message = "completed",
            assistantText = "The reply contains <!channel>, <@U123>, and <https://example.test|a link>.",
            artifactCount = 0,
            exitCode = 0,
        };
        var evt = new CloudEvent(
            "manager-delivery-1",
            new Uri("/mohist/agent-job/manager-job-reply", UriKind.Relative),
            EventCatalog.ReverseDns.AgentJobTerminalDelivery,
            time.GetUtcNow(),
            JsonSerializer.SerializeToElement(delivery),
            subject: "manager-job-reply",
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = SlackDeliveryOwnerIds.ManagerProjectId,
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var row = Assert.Single((await scope.ServiceProvider
            .GetRequiredService<SlackOutboxStore>()
            .ListManagerAsync(enrollmentId)).Entries);
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Equal(SlackDeliveryOwnerKinds.Manager, row.OwnerKind);
        Assert.Equal(
            "The reply contains &lt;!channel&gt;, &lt;@U123&gt;, and &lt;https://example.test|a link&gt;.",
            payload.Text);
        Assert.DoesNotContain("<!channel>", payload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<@U123>", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTerminalDeliveryEnvelope_CarriesTheFirstSessionInputAsAnEightyCharacterLabel()
    {
        var prompt = "Investigate the release pipeline and document every failing check before proposing a fix. "
            + "Then summarize the safest rollout.";
        var pending = new PendingTerminalDeliveryEvent(
            EventId: "delivery-1",
            Origin: new ConnectionLaunchOrigin("conn-1", "team-1", "U_OWNER", "C1", "1710000000.000001", "1710000000.000000"),
            Status: AgentJobStatus.Completed,
            Message: "completed",
            FailureReason: null,
            FailureCategory: null,
            ArtifactCount: 0,
            ExitCode: 0,
            RecordedAt: new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
            Output: "{\"kind\":\"opencode\",\"text\":\"Manager response with token=xoxb-secret\"}");

        var envelope = AgentJobLineage.BuildTerminalDeliveryEnvelope(
            "job-1",
            pending,
            new Dictionary<string, string> { [EventCatalog.Lineage.ProjectId] = "proj-1" },
            prompt);

        var workLabel = envelope.Data!.Value.GetProperty("workLabel").GetString();
        Assert.Equal(prompt[..80], workLabel);
        Assert.Equal("C1", envelope.Data.Value.GetProperty("conversationId").GetString());
        Assert.Equal("1710000000.000000", envelope.Data.Value.GetProperty("threadTs").GetString());
        Assert.Equal("Manager response with ***", envelope.Data.Value.GetProperty("assistantText").GetString());
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

        public Task<int> RecoverBackpressuredAsync(string projectId, string connectionId, CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
