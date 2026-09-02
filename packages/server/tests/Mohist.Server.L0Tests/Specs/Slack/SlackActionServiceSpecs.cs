using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Slack.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Slack;

public sealed class SlackActionServiceSpecs
{
    private static readonly DateTime FixedNow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Stop_action_creation_enforces_the_bound_connection_and_control_actor()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var session = DispatchProxy.Create<IAgentSessionGrain, SessionProxy>();
        var sessionState = (SessionProxy)(object)session;
        sessionState.Control = new AgentTurnControlState(
            "turn-1",
            AgentTurnStatus.Executing,
            AgentTurnControlClassification.Executing,
            IsLaunchTurn: false);
        sessionState.Initial = InitialLaunch("U_INITIATOR");
        var service = new SlackTurnControlService(
            new FixedSigner(),
            GrainFactory(session),
            null!,
            null!,
            null!,
            time);
        var connection = Connection(owner: "U_OWNER");

        var ownerAction = await service.CreateStopActionAsync(
            connection,
            "session-1",
            "turn-1",
            "input-1",
            "dispatch-1",
            "U_OWNER",
            new SlackMessageIdentity("T1", "C1", "message-1"),
            "message-1");
        var initiatorAction = await service.CreateStopActionAsync(
            connection,
            "session-1",
            "turn-1",
            "input-1",
            "dispatch-1",
            "U_INITIATOR",
            new SlackMessageIdentity("T1", "C1", "message-1"),
            "message-1");
        var otherAction = await service.CreateStopActionAsync(
            connection,
            "session-1",
            "turn-1",
            "input-1",
            "dispatch-1",
            "U_OTHER",
            new SlackMessageIdentity("T1", "C1", "message-1"),
            "message-1");

        Assert.NotNull(ownerAction);
        Assert.NotNull(initiatorAction);
        Assert.Null(otherAction);
        Assert.Equal(now.AddMinutes(5), ownerAction!.ExpiresAt);
        Assert.Contains(SlackTurnControlService.StopActionId, ownerAction.Blocks.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb", ownerAction.ActionValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_action_creation_requires_a_retryable_failed_turn_and_slack_provenance()
    {
        var session = DispatchProxy.Create<IAgentSessionGrain, SessionProxy>();
        var sessionState = (SessionProxy)(object)session;
        sessionState.Turns =
        [
            new AgentTurnRecord(
                "turn-1",
                1,
                ["input-1"],
                AgentTurnStatus.Failed,
                Result: new AgentTurnResult(FailureCategory: AgentJobFailureReasons.RunnerUnavailable)),
        ];
        sessionState.Initial = InitialLaunch("U_INITIATOR");
        var service = new SlackRetryActionService(
            new FixedSigner(),
            GrainFactory(session),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var connection = Connection(owner: "U_OWNER");

        var action = await service.CreateRetryActionAsync(
            connection,
            "session-1",
            "turn-1",
            new SlackMessageIdentity("T1", "C1", "message-1"),
            "message-1");

        Assert.NotNull(action);
        Assert.Equal(SlackRetryActionService.RetryActionId, action!.ActionId);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), action.ExpiresAt);

        sessionState.Turns =
        [
            new AgentTurnRecord(
                "turn-1",
                1,
                ["input-1"],
                AgentTurnStatus.Completed,
                Result: new AgentTurnResult(FailureCategory: AgentJobFailureReasons.RunnerUnavailable)),
        ];
        Assert.Null(await service.CreateRetryActionAsync(
            connection,
            "session-1",
            "turn-1",
            new SlackMessageIdentity("T1", "C1", "message-1"),
            "message-1"));
    }

    [Fact]
    public async Task Retry_operation_receipt_is_idempotent_in_an_in_memory_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new MohistDbContext(options))
            await db.Database.EnsureCreatedAsync();

        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new AgentRetryOperationStore(factory, time);

        var first = await store.ClaimOrCreateAsync(
            "project-1",
            "session-1",
            "turn-1",
            "nonce-1",
            AgentRetryOperationKind.Root,
            "retry-session",
            "retry-input",
            "retry-turn");
        var replay = await store.ClaimOrCreateAsync(
            "project-1",
            "session-1",
            "turn-1",
            "nonce-1",
            AgentRetryOperationKind.Root,
            "different-session",
            "different-input",
            "different-turn");

        Assert.False(first.AlreadyExists);
        Assert.True(replay.AlreadyExists);
        Assert.Equal(first.Operation.OperationId, replay.Operation.OperationId);
        Assert.Equal("retry-session", replay.Operation.PreAllocatedSessionId);

        await store.MarkFinishedAsync(first.Operation.OperationId, "accepted", "Retry attempt accepted.");
        var recorded = await store.FindExistingAsync("project-1", "session-1", "turn-1", "nonce-1");
        Assert.NotNull(recorded);
        Assert.False(recorded!.IsPending);
        Assert.Equal("accepted", recorded.ResultState);
    }

    private static AgentConnection Connection(string owner) => new()
    {
        Id = "connection-1",
        ProjectId = "project-1",
        WorkspaceTeamId = "T1",
        OwnerSlackUserId = owner,
    };

    private static AgentInitialLaunchSnapshot InitialLaunch(string initiator) =>
        new(
            "session-1",
            new AgentSessionInputRecord(
                "input-1",
                1,
                "start",
                "agent-launch",
                AgentSessionInputAcceptance.Accepted,
                FixedNow,
                JobId: "job-1",
                Provenance: new AgentSessionInputProvenance(
                    "slack",
                    "T1",
                    "C1",
                    null,
                    initiator,
                    "message-1",
                    "connection-1",
                    "message-1")),
            null);

    private static IGrainFactory GrainFactory(IAgentSessionGrain session)
    {
        var grains = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grains).Session = session;
        return grains;
    }

    private sealed class FixedSigner : ISlackActionSigner
    {
        public Task<string?> TrySignAsync(AgentConnection connection, string canonical, CancellationToken ct = default) =>
            Task.FromResult<string?>("signature");

        public Task<bool> VerifyAsync(AgentConnection connection, string canonical, string? signature, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(signature, "signature", StringComparison.Ordinal));
    }

    private class SessionProxy : DispatchProxy
    {
        public AgentTurnControlState? Control { get; set; }
        public AgentInitialLaunchSnapshot? Initial { get; set; }
        public IReadOnlyList<AgentTurnRecord> Turns { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(IAgentSessionGrain.ResolveTurnControlAsync) => Task.FromResult(Control),
                nameof(IAgentSessionGrain.GetInitialLaunchAsync) => Task.FromResult(Initial),
                nameof(IAgentSessionGrain.ListTurnsAsync) => Task.FromResult(Turns),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public IAgentSessionGrain Session { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod
                && targetMethod.GetGenericArguments()[0] == typeof(IAgentSessionGrain))
                return Session;

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
