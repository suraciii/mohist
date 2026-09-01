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
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Slack;

[Collection("SlackTurnControlInteraction")]
public sealed class SlackTerminalDeliveryPresentationSpecs
{
    private readonly IsolatedMohistIntegrationFixture _fixture;

    public SlackTerminalDeliveryPresentationSpecs(IsolatedMohistIntegrationFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(AgentJobFailureReasons.RunnerUnavailable)]
    [InlineData(AgentJobFailureReasons.RunnerLost)]
    [InlineData(AgentJobFailureReasons.ReportTimeout)]
    [InlineData("generation-drain-timeout")]
    public async Task HandleAsync_retryable_failure_promotes_a_signed_retry_notice_with_durable_target_facts(
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
            await scope.ServiceProvider.GetRequiredService<SlackStatusProjection>()
                .EnqueueWorkingAsync(
                    connection.ProjectId,
                    connection.Id,
                    source,
                    failed.ThreadTs);
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
        var failure = Assert.Single(rows, row => row.Kind == SlackOutboxKinds.ExplicitFailure);
        Assert.Equal(SlackOutboxStates.Pending, failure.State);
        Assert.Equal(failed.ConversationId, failure.ConversationId);
        Assert.Equal(failed.ThreadTs, failure.ThreadTs);

        var payload = SlackDeliveryPayload.Parse(failure.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
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
                .EnqueueWorkingAsync(connection.ProjectId, connection.Id, source, threadTs);
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
                .EnqueueWorkingAsync(connection.ProjectId, connection.Id, source, failed.ThreadTs);
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
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow { Id = projectId, Name = projectId, CreatedAt = now, UpdatedAt = now });
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = "Terminal presentation agent",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = "Terminal presentation agent",
                Status = AgentStatus.Active,
                Instructions = "Handle Slack requests.",
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T-terminal",
            AppId = "A-terminal",
            BotUserId = "U-terminal-bot",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_OWNER",
            AccessPolicy = AccessPolicyKind.OwnerOnly,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ISecretStore>().StoreAsync(
            new SecretStoreAddress(projectId, id, SecretKind.BotToken),
            Encoding.UTF8.GetBytes("xoxb-terminal-signing-key"));

        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            WorkspaceTeamId = "T-terminal",
            AppId = "A-terminal",
            BotUserId = "U-terminal-bot",
            OwnerSlackUserId = "U_OWNER",
            AccessPolicy = AccessPolicyKind.OwnerOnly,
        };
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
