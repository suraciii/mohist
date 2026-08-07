using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackOutboxBackpressureRecoverySpecs
{
    private static readonly DateTimeOffset Start = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dispatcher_recovers_outbox_overflow_when_pending_outbox_drains_below_capacity()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new SlackConnectionHealthBackpressurer(new TestDbContextFactory(database.Options), new FakeTimeProvider(Start));
        await SeedBackpressuredConnectionAsync(database, SlackProviderBackpressureReasons.OutboxOverflow);
        var (inbox, outbox, dispatcher) = await BuildDispatcherAsync(database, health, inboxCapacity: 4, outboxCapacity: 2);

        await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "queued", dispatchRef: "agent:1"));
        await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "queued-2", dispatchRef: "agent:2"));
        var claimed = await outbox.ClaimAsync("proj_a", "conn_1", "adapter-a");
        await outbox.MarkDeliveredAsync("proj_a", claimed!.Id);

        await dispatcher.DispatchAsync(CancellationToken.None);

        var reloaded = await ReloadConnectionAsync(database);
        Assert.Equal(ConnectionHealthKind.Healthy, reloaded.ConnectionHealth);
        Assert.Null(reloaded.HealthReason);
    }

    [Fact]
    public async Task Dispatcher_recovers_inbox_overflow_when_pending_inbox_drains_below_capacity()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new SlackConnectionHealthBackpressurer(new TestDbContextFactory(database.Options), new FakeTimeProvider(Start));
        await SeedBackpressuredConnectionAsync(database, SlackProviderBackpressureReasons.InboxOverflow);
        var (inbox, outbox, dispatcher) = await BuildDispatcherAsync(database, health, inboxCapacity: 1, outboxCapacity: 4);

        var accepted = await inbox.AcceptAsync(InboxDraft("1.0000"), Route());
        await inbox.MarkDispatchedAsync("proj_a", accepted.Id);

        await dispatcher.DispatchAsync(CancellationToken.None);

        var reloaded = await ReloadConnectionAsync(database);
        Assert.Equal(ConnectionHealthKind.Healthy, reloaded.ConnectionHealth);
        Assert.Null(reloaded.HealthReason);
    }

    [Fact]
    public async Task Dispatcher_keeps_connection_backpressured_when_either_side_remains_at_capacity()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new SlackConnectionHealthBackpressurer(new TestDbContextFactory(database.Options), new FakeTimeProvider(Start));
        await SeedBackpressuredConnectionAsync(database, SlackProviderBackpressureReasons.OutboxOverflow);
        var (inbox, outbox, dispatcher) = await BuildDispatcherAsync(database, health, inboxCapacity: 1, outboxCapacity: 1);

        await inbox.AcceptAsync(InboxDraft("1.0000"), Route());
        await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "queued", dispatchRef: "agent:1"));

        await dispatcher.DispatchAsync(CancellationToken.None);

        var reloaded = await ReloadConnectionAsync(database);
        Assert.Equal(ConnectionHealthKind.Degraded, reloaded.ConnectionHealth);
        Assert.Equal(SlackProviderBackpressureReasons.OutboxOverflow, reloaded.HealthReason);
    }

    [Fact]
    public async Task Dispatcher_keeps_connection_backpressured_when_inbox_remains_full_even_if_outbox_drained()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new SlackConnectionHealthBackpressurer(new TestDbContextFactory(database.Options), new FakeTimeProvider(Start));
        await SeedBackpressuredConnectionAsync(database, SlackProviderBackpressureReasons.InboxOverflow);
        var (inbox, outbox, dispatcher) = await BuildDispatcherAsync(database, health, inboxCapacity: 1, outboxCapacity: 4);

        await inbox.AcceptAsync(InboxDraft("1.0000"), Route());

        await dispatcher.DispatchAsync(CancellationToken.None);

        var reloaded = await ReloadConnectionAsync(database);
        Assert.Equal(ConnectionHealthKind.Degraded, reloaded.ConnectionHealth);
        Assert.Equal(SlackProviderBackpressureReasons.InboxOverflow, reloaded.HealthReason);
    }

    [Fact]
    public async Task Dispatcher_ignores_a_degraded_connection_with_a_non_backpressure_reason()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new SlackConnectionHealthBackpressurer(new TestDbContextFactory(database.Options), new FakeTimeProvider(Start));
        await SeedConnectionAsync(database, healthReason: "rotating credentials");
        var (inbox, outbox, dispatcher) = await BuildDispatcherAsync(database, health, inboxCapacity: 4, outboxCapacity: 4);

        await inbox.AcceptAsync(InboxDraft("1.0000"), Route());
        await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "queued", dispatchRef: "agent:1"));
        var claimed = await outbox.ClaimAsync("proj_a", "conn_1", "adapter-a");
        await outbox.MarkDeliveredAsync("proj_a", claimed!.Id);

        await dispatcher.DispatchAsync(CancellationToken.None);

        var reloaded = await ReloadConnectionAsync(database);
        Assert.Equal(ConnectionHealthKind.Degraded, reloaded.ConnectionHealth);
        Assert.Equal("rotating credentials", reloaded.HealthReason);
    }

    [Fact]
    public async Task Dispatcher_ignores_a_disabled_backpressured_connection()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new SlackConnectionHealthBackpressurer(new TestDbContextFactory(database.Options), new FakeTimeProvider(Start));
        await SeedBackpressuredConnectionAsync(database, SlackProviderBackpressureReasons.OutboxOverflow, DesiredStateKind.Disabled);
        var (_, _, dispatcher) = await BuildDispatcherAsync(database, health, inboxCapacity: 4, outboxCapacity: 4);

        await dispatcher.DispatchAsync(CancellationToken.None);

        var reloaded = await ReloadConnectionAsync(database);
        Assert.Equal(ConnectionHealthKind.Degraded, reloaded.ConnectionHealth);
        Assert.Equal(SlackProviderBackpressureReasons.OutboxOverflow, reloaded.HealthReason);
    }

    [Fact]
    public async Task Backpressure_recovery_does_not_drop_accepted_inbox_or_terminal_outbox_rows()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new SlackConnectionHealthBackpressurer(new TestDbContextFactory(database.Options), new FakeTimeProvider(Start));
        await SeedBackpressuredConnectionAsync(database, SlackProviderBackpressureReasons.OutboxOverflow);
        var (inbox, outbox, dispatcher) = await BuildDispatcherAsync(database, health, inboxCapacity: 4, outboxCapacity: 2);

        var inboxEntry = await inbox.AcceptAsync(InboxDraft("1.0000"), Route());
        var terminalA = await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "queued-a", dispatchRef: "agent:1"));
        var terminalB = await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "queued-b", dispatchRef: "agent:2"));

        var claimed = await outbox.ClaimAsync("proj_a", "conn_1", "adapter-a");
        await outbox.MarkDeliveredAsync("proj_a", claimed!.Id);

        await dispatcher.DispatchAsync(CancellationToken.None);

        var inboxList = await inbox.ListAsync("proj_a", "conn_1");
        var outboxList = await outbox.ListAsync("proj_a", "conn_1");
        Assert.Single(inboxList.Entries);
        Assert.Equal(inboxEntry.Id, inboxList.Entries[0].Id);
        Assert.Equal(2, outboxList.Entries.Count);
        Assert.Contains(outboxList.Entries, row => row.Id == terminalA.Id && row.PayloadJson == "\"queued-a\"");
        Assert.Contains(outboxList.Entries, row => row.Id == terminalB.Id && row.PayloadJson == "\"queued-b\"");

        var reloaded = await ReloadConnectionAsync(database);
        Assert.Equal(ConnectionHealthKind.Healthy, reloaded.ConnectionHealth);
    }

    [Fact]
    public async Task Replaceable_progress_still_merges_across_backpressure_and_terminal_rows_are_neither_merged_nor_dropped()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var health = new SlackConnectionHealthBackpressurer(new TestDbContextFactory(database.Options), new FakeTimeProvider(Start));
        await SeedBackpressuredConnectionAsync(database, SlackProviderBackpressureReasons.OutboxOverflow);
        var (_, outbox, _) = await BuildDispatcherAsync(database, health, inboxCapacity: 4, outboxCapacity: 4);

        var progressFirst = await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.ReplaceableProgress, "queued", dispatchRef: "agent:run"));
        var progressMerged = await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.ReplaceableProgress, "executing", dispatchRef: "agent:run"));
        var terminal = await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.TerminalResult, "done", dispatchRef: "agent:run"));
        var failure = await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.ExplicitFailure, "explode", dispatchRef: "agent:run"));
        var userAction = await outbox.EnqueueAsync(OutboxDraft(SlackOutboxKinds.UserAction, "ask", dispatchRef: "agent:run"));

        Assert.True(progressMerged.MergedIntoExisting);
        Assert.Equal(progressFirst.Id, progressMerged.Id);

        var rows = (await outbox.ListAsync("proj_a", "conn_1")).Entries;
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, row => row.Id == progressFirst.Id && row.PayloadJson == "\"executing\"");
        Assert.Contains(rows, row => row.Id == terminal.Id && row.Kind == SlackOutboxKinds.TerminalResult);
        Assert.Contains(rows, row => row.Id == failure.Id && row.Kind == SlackOutboxKinds.ExplicitFailure);
        Assert.Contains(rows, row => row.Id == userAction.Id && row.Kind == SlackOutboxKinds.UserAction);

        var reloaded = await ReloadConnectionAsync(database);
        Assert.Equal(ConnectionHealthKind.Degraded, reloaded.ConnectionHealth);
    }

    [Fact]
    public void Backpressured_diagnostic_uses_distinct_state_with_inbox_or_outbox_reason()
    {
        var inbox = new AgentConnection
        {
            Id = "conn_1",
            ProjectId = "proj_a",
            AgentId = "agent_1",
            ProviderKind = ConnectionProviderKind.Slack,
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Degraded,
            HealthReason = SlackProviderBackpressureReasons.InboxOverflow,
            AgentReadiness = AgentReadinessKind.Ready,
        };
        var outbox = new AgentConnection
        {
            Id = "conn_2",
            ProjectId = "proj_a",
            AgentId = "agent_2",
            ProviderKind = ConnectionProviderKind.Slack,
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Degraded,
            HealthReason = SlackProviderBackpressureReasons.OutboxOverflow,
            AgentReadiness = AgentReadinessKind.Ready,
        };

        var inboxDiagnostic = ConnectionDiagnostic.Compute(inbox, new DiagnosticInputs());
        var outboxDiagnostic = ConnectionDiagnostic.Compute(outbox, new DiagnosticInputs());

        Assert.Equal(ConnectionDiagnosticState.Backpressured, inboxDiagnostic.PrimaryState);
        Assert.Equal(ConnectionDiagnosticState.Backpressured, outboxDiagnostic.PrimaryState);
        Assert.Contains("inbox", inboxDiagnostic.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outbox", outboxDiagnostic.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Wait for the backlog to drain / retry input shortly.", inboxDiagnostic.NextAction);
        Assert.Equal(inboxDiagnostic.NextAction, outboxDiagnostic.NextAction);
    }

    [Fact]
    public void Backpressured_diagnostic_is_independent_of_disabled_state()
    {
        var backpressured = new AgentConnection
        {
            Id = "conn_1",
            ProjectId = "proj_a",
            AgentId = "agent_1",
            ProviderKind = ConnectionProviderKind.Slack,
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Degraded,
            HealthReason = SlackProviderBackpressureReasons.OutboxOverflow,
            AgentReadiness = AgentReadinessKind.Ready,
        };
        var disabled = new AgentConnection
        {
            Id = "conn_2",
            ProjectId = "proj_a",
            AgentId = "agent_2",
            ProviderKind = ConnectionProviderKind.Slack,
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Disabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
        };

        var backpressuredResult = ConnectionDiagnostic.Compute(backpressured, new DiagnosticInputs());
        var disabledResult = ConnectionDiagnostic.Compute(disabled, new DiagnosticInputs());

        Assert.Equal(ConnectionDiagnosticState.Backpressured, backpressuredResult.PrimaryState);
        Assert.Equal(ConnectionDiagnosticState.Disabled, disabledResult.PrimaryState);
        Assert.NotEqual(backpressuredResult.NextAction, disabledResult.NextAction);
    }

    private static async Task<(SlackProviderInboxStore Inbox, SlackOutboxStore Outbox, SlackOutboxDispatcherService Dispatcher)> BuildDispatcherAsync(
        TestSqliteDatabase database,
        ISlackConnectionHealthBackpressurer health,
        int inboxCapacity,
        int outboxCapacity)
    {
        var factory = new TestDbContextFactory(database.Options);
        var time = new FakeTimeProvider(Start);
        var options = Options.Create(new SlackProviderOptions
        {
            InboxCapacityPerConnection = inboxCapacity,
            OutboxCapacityPerConnection = outboxCapacity,
            OutboxClaimTimeout = TimeSpan.FromSeconds(30),
            OutboxUncertainTimeout = TimeSpan.FromMinutes(5),
        });
        var inbox = new SlackProviderInboxStore(factory, time, options, health);
        var outbox = new SlackOutboxStore(factory, health, time, options);
        var deadLetters = new NoopDeadLetterStore();
        var connectionStore = new AgentConnectionStore(
            factory,
            new AgentQuerier(factory),
            new NoopSecretStore(),
            Array.Empty<IAgentConnectionProviderCleanup>(),
            time);
        var dispatcher = new SlackOutboxDispatcherService(
            outbox, inbox, connectionStore, health, deadLetters, time, options, NullLogger<SlackOutboxDispatcherService>.Instance);
        return (inbox, outbox, dispatcher);
    }

    private static Task SeedBackpressuredConnectionAsync(
        TestSqliteDatabase database,
        string healthReason,
        string desiredState = DesiredStateKind.Enabled) =>
        SeedConnectionAsync(database, healthReason, desiredState);

    private static async Task SeedConnectionAsync(
        TestSqliteDatabase database,
        string? healthReason = null,
        string desiredState = DesiredStateKind.Enabled)
    {
        await using var db = database.CreateContext();
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = "conn_1",
            ProjectId = "proj_a",
            AgentId = "agent_1",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "team-1",
            AppId = "app-1",
            BotUserId = "bot-1",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = desiredState,
            ConnectionHealth = healthReason is null ? ConnectionHealthKind.Healthy : ConnectionHealthKind.Degraded,
            HealthReason = healthReason,
            AgentReadiness = AgentReadinessKind.Ready,
            CreatedAt = Start,
            UpdatedAt = Start,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<AgentConnectionRow> ReloadConnectionAsync(TestSqliteDatabase database)
    {
        await using var db = database.CreateContext();
        return await db.AgentConnections.AsNoTracking().SingleAsync(row => row.Id == "conn_1");
    }

    private static SlackProviderInboxDraft InboxDraft(string messageTs) => new(
        "proj_a", "conn_1", new SlackMessageIdentity("team-1", "D1", messageTs), "U1");

    private static SlackProviderInboxRouteDraft Route() =>
        new(SlackProviderInboxRouteKinds.Followup, "session-1");

    private static SlackOutboxDraft OutboxDraft(string kind, string payload, string dispatchRef = "dispatch-1") => new(
        "proj_a", "conn_1", "team-1", "D1", kind, dispatchRef, $"\"{payload}\"");

    private sealed class NoopSecretStore : ISecretStore
    {
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(false);
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}

