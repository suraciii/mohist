using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Slack.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Collection("SlackTurnControlInteraction")]
[Trait("level", "L1")]
public sealed class SlackTerminalDeliveryPresentationSpecs
{
    private readonly IsolatedMohistIntegrationFixture _fixture;

    public SlackTerminalDeliveryPresentationSpecs(IsolatedMohistIntegrationFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(AgentJobFailureReasons.RunnerUnavailable)]
    [InlineData(AgentJobFailureReasons.RunnerLost)]
    [InlineData(AgentJobFailureReasons.ReportTimeout)]
    [InlineData("generation-drain-timeout")]
    public async Task HandleAsync_retryable_failure_posts_a_separate_signed_retry_notice_with_durable_target_facts(
        string failureCategory)
    {
        var connection = await CreateConnectionAsync();
        var failed = await SeedFailedLaunchAsync(connection, "C-terminal-positive", failureCategory);
        var source = new SlackMessageIdentity(
            connection.WorkspaceTeamId,
            failed.ConversationId,
            failed.MessageTs);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var sessionBlocks = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    type = "actions",
                    elements = new[]
                    {
                        new
                        {
                            type = "button",
                            text = new { type = "plain_text", text = "Open in Mohist" },
                            url = $"https://mohist.example/{connection.ProjectId}/sessions/{failed.SessionId}",
                        },
                    },
                },
            });
            await scope.ServiceProvider.GetRequiredService<SlackStatusProjection>()
                .EnqueueWorkingAsync(
                    connection.ProjectId,
                    connection.Id,
                    source,
                    failed.ThreadTs,
                    blocks: sessionBlocks,
                    sessionId: failed.SessionId);
        }

        await HandleAsync(new SlackTerminalDelivery(
            JobKey: "terminal-positive",
            WorkLabel: "retryable terminal failure",
            ConnectionId: connection.Id,
            WorkspaceTeamId: connection.WorkspaceTeamId,
            ConversationId: failed.ConversationId,
            Status: "failed",
            Message: "failed",
            FailureReason: "runner unavailable: xoxb-bot-secret xapp-app-secret xoxe-config-secret xoxr-refresh-secret",
            FailureCategory: failureCategory,
            ArtifactCount: 0,
            ExitCode: 1,
            ThreadTs: failed.ThreadTs,
            MessageTs: failed.MessageTs,
            SessionId: failed.SessionId,
            TurnId: failed.TurnId,
            SlackUserId: "U_OWNER"));

        await using var readScope = _fixture.Services.CreateAsyncScope();
        var rows = (await readScope.ServiceProvider
            .GetRequiredService<SlackOutboxStore>()
            .ListAsync(connection.ProjectId, connection.Id)).Entries;
        var card = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.ReplaceableProgress);
        var cardPayload = SlackDeliveryPayload.Parse(card.PayloadJson);
        Assert.Equal($"Agent session.\nSession: {failed.SessionId}", cardPayload.Text);
        Assert.Contains("Open in Mohist", cardPayload.Blocks?.GetRawText(), StringComparison.Ordinal);
        Assert.Contains($"/sessions/{failed.SessionId}", cardPayload.Blocks?.GetRawText(), StringComparison.Ordinal);
        var failure = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.ExplicitFailure);
        Assert.NotEqual(card.Id, failure.Id);
        Assert.Equal(SlackOutboxStates.Pending, failure.State);
        Assert.Equal(failed.ConversationId, failure.ConversationId);
        Assert.Equal(failed.ThreadTs, failure.ThreadTs);

        var payload = SlackDeliveryPayload.Parse(failure.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.Null(payload.ProviderMessageIdentity);
        Assert.Null(payload.StatusDispatchRef);
        Assert.Equal(
            "The Agent run failed: runner unavailable: [REDACTED] [REDACTED] [REDACTED] [REDACTED]",
            payload.Text);
        Assert.DoesNotContain("xoxb-bot-secret", failure.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-app-secret", failure.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxe-config-secret", failure.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxr-refresh-secret", failure.PayloadJson, StringComparison.Ordinal);
        var blocks = Assert.NotNull(payload.Blocks);
        var button = Assert.Single(
            blocks.EnumerateArray()
                .SelectMany(block => block.GetProperty("elements").EnumerateArray()));
        Assert.Equal("button", button.GetProperty("type").GetString());
        Assert.Equal(SlackRetryActionService.RetryActionId, button.GetProperty("action_id").GetString());

        var actionValue = button.GetProperty("value").GetString();
        var action = Assert.IsType<SlackRetryActionPayload>(JSON.Deserialize<SlackRetryActionPayload>(actionValue!));
        Assert.Equal(connection.Id, action.ConnectionId);
        Assert.Equal(failed.SessionId, action.SessionId);
        Assert.Equal(failed.TurnId, action.TurnId);
        Assert.Equal(failed.ConversationId, action.ConversationId);
        Assert.Equal(failed.MessageTs, action.MessageTs);
        Assert.Equal(failed.ThreadTs, action.ThreadTs);
        Assert.Equal("U_OWNER", action.ActorSlackUserId);
        Assert.Equal("U_OWNER", action.InitiatorSlackUserId);
        Assert.Equal(_fixture.TimeProvider.GetUtcNow().AddMinutes(5), action.ExpiresAt);
        Assert.False(string.IsNullOrWhiteSpace(action.Nonce));
        Assert.False(string.IsNullOrWhiteSpace(action.Signature));
    }

    [Fact]
    public async Task HandleAsync_retryable_failure_with_missing_durable_facts_falls_back_to_reaction_only()
    {
        var connection = await CreateConnectionAsync();
        var messageTs = "1710000000.000002";
        var threadTs = "1710000000.000001";
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, "C-terminal-missing-facts", messageTs);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SlackStatusProjection>()
                .EnqueueWorkingAsync(
                    connection.ProjectId, connection.Id, source, threadTs, sessionId: "missing-session");
        }

        await HandleAsync(new SlackTerminalDelivery(
            JobKey: "terminal-missing-facts",
            WorkLabel: "missing durable facts",
            ConnectionId: connection.Id,
            WorkspaceTeamId: connection.WorkspaceTeamId,
            ConversationId: source.ConversationId,
            Status: "failed",
            Message: "failed",
            FailureReason: "runner unavailable",
            FailureCategory: AgentJobFailureReasons.RunnerUnavailable,
            ArtifactCount: 0,
            ExitCode: 1,
            ThreadTs: threadTs,
            MessageTs: messageTs,
            SessionId: "missing-session",
            TurnId: "missing-turn"));

        await AssertReactionOnlyAsync(connection, source.ConversationId);
    }

    [Fact]
    public async Task HandleAsync_retryable_failure_without_signing_material_falls_back_to_reaction_only()
    {
        var connection = await CreateConnectionAsync();
        var failed = await SeedFailedLaunchAsync(connection, "C-terminal-no-signing");
        var source = new SlackMessageIdentity(connection.WorkspaceTeamId, failed.ConversationId, failed.MessageTs);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SlackStatusProjection>()
                .EnqueueWorkingAsync(
                    connection.ProjectId, connection.Id, source, failed.ThreadTs, sessionId: failed.SessionId);
            await scope.ServiceProvider.GetRequiredService<ISecretStore>().DeleteAsync(
                new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken));
        }

        await HandleAsync(new SlackTerminalDelivery(
            JobKey: "terminal-no-signing",
            WorkLabel: "missing signing material",
            ConnectionId: connection.Id,
            WorkspaceTeamId: connection.WorkspaceTeamId,
            ConversationId: failed.ConversationId,
            Status: "failed",
            Message: "failed",
            FailureReason: "runner unavailable",
            FailureCategory: AgentJobFailureReasons.RunnerUnavailable,
            ArtifactCount: 0,
            ExitCode: 1,
            ThreadTs: failed.ThreadTs,
            MessageTs: failed.MessageTs,
            SessionId: failed.SessionId,
            TurnId: failed.TurnId));

        await AssertReactionOnlyAsync(connection, failed.ConversationId);
    }

    private async Task HandleAsync(SlackTerminalDelivery delivery)
    {
        var evt = new CloudEvent(
            $"terminal-presentation-{Guid.NewGuid():N}",
            new Uri($"/mohist/agent-job/{delivery.JobKey}", UriKind.Relative),
            EventCatalog.ReverseDns.AgentJobTerminalDelivery,
            _fixture.TimeProvider.GetUtcNow(),
            JsonSerializer.SerializeToElement(delivery),
            subject: delivery.JobKey,
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = delivery.ConnectionId.StartsWith("connection_", StringComparison.Ordinal)
                    ? await ResolveProjectIdAsync(delivery.ConnectionId)
                    : throw new InvalidOperationException("Test delivery connection id is invalid."),
            });

        await _fixture.Services.GetRequiredService<SlackTerminalDeliveryHandler>()
            .HandleAsync(evt, CancellationToken.None);
    }

    private async Task<string> ResolveProjectIdAsync(string connectionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await db.AgentConnections
            .Where(row => row.Id == connectionId)
            .Select(row => row.ProjectId)
            .SingleAsync();
    }

    private async Task AssertReactionOnlyAsync(AgentConnection connection, string conversationId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var rows = (await scope.ServiceProvider
            .GetRequiredService<SlackOutboxStore>()
            .ListAsync(connection.ProjectId, connection.Id)).Entries;
        Assert.DoesNotContain(rows, row => row.Kind == SlackOutboxKinds.ExplicitFailure);
        Assert.Contains(rows, row => row.Kind == SlackOutboxKinds.UserAction
            && row.ConversationId == conversationId
            && SlackDeliveryPayload.Parse(row.PayloadJson).Operation == SlackDeliveryOperations.ReactionAdd);
    }

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var seeded = await SlackManagedConnectionSeed.CreateAsync(_fixture, new SlackSeedOptions
        {
            WorkspaceTeamId = "T-terminal",
            AppId = "A-terminal",
            BotUserId = "U-terminal-bot",
            AgentName = "Terminal presentation agent",
            BotToken = "xoxb-terminal-signing-key",
            WithManagedApp = false,
            WriteConnectionAppSecret = false,
            WithRuntimeLease = false,
        });
        return seeded.Connection;
    }

    private async Task<SeededFailedLaunch> SeedFailedLaunchAsync(
        AgentConnection connection,
        string conversationId,
        string failureCategory = AgentJobFailureReasons.RunnerUnavailable)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var agent = await scope.ServiceProvider.GetRequiredService<AgentQuerier>()
            .GetByIdAsync(connection.ProjectId, connection.AgentId);
        const string messageTs = "1710000000.000001";
        var threadTs = "1710000000.000001";
        var origin = new ConnectionLaunchOrigin(
            connection.Id,
            connection.WorkspaceTeamId,
            "U_OWNER",
            conversationId,
            messageTs,
            threadTs);
        var launch = await scope.ServiceProvider.GetRequiredService<IAgentLauncher>()
            .LaunchConnectionAsync(agent!, "retryable terminal failure", origin);
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(launch.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        await session.MarkInitialTurnTerminalAsync(
            initial!.Turn!.JobId!,
            AgentTurnStatus.Failed,
            new AgentTurnResult(
                FailureReason: "runner unavailable",
                FailureCategory: failureCategory));
        return new SeededFailedLaunch(launch.SessionId, launch.TurnId, conversationId, messageTs, threadTs);
    }

    private sealed record SeededFailedLaunch(
        string SessionId,
        string TurnId,
        string ConversationId,
        string MessageTs,
        string ThreadTs);
}
