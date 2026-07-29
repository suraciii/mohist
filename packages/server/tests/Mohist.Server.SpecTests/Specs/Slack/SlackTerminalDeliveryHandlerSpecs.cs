using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
        var delivery = new SlackTerminalDelivery(
            "job-1", "conn-1", "team-1", "D1", status,
            "completed with token=xoxb-secret", "output evidence", "failure details", "runtime-failed", 2, 1);
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
        Assert.Contains($"Conclusion: {expectedConclusion}", text);
        Assert.Contains("Evidence: ", text);
        Assert.True(text.Contains($"Next step: {expectedNextStep}", StringComparison.Ordinal), text);
        Assert.DoesNotContain("xoxb-secret", text);
    }

    private sealed class NoopHealthBackpressurer : ISlackConnectionHealthBackpressurer
    {
        public Task FlipBackpressuredAsync(string projectId, string connectionId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
