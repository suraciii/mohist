using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.TestSupport;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackAmbiguousPromptStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_writer_owns_the_prompt_and_other_connections_observe_its_claim()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var store = NewStore(database);

        var first = await store.TryClaimAsync(
            "project-a", "team-a", "channel-a", "1.001", "1.000",
            "connection-a", ["connection-a", "connection-b"]);
        var other = await store.TryClaimAsync(
            "project-a", "team-a", "channel-a", "1.001", "1.000",
            "connection-b", ["connection-a", "connection-b"]);

        Assert.True(first.Claimed);
        Assert.False(other.Claimed);
        Assert.Equal(first.RowId, other.RowId);
        Assert.Equal("connection-a", other.WinningConnectionId);
        Assert.Equal("1.000", other.ThreadTs);
        Assert.Equal(["connection-a", "connection-b"], other.MentionedConnectionIds);
    }

    [Fact]
    public async Task Winner_retries_until_its_stable_outbox_delivery_exists()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var store = NewStore(database);
        const string teamId = "team-a";
        const string conversationId = "channel-a";
        const string messageTs = "2.001";
        const string connectionId = "connection-a";

        await store.TryClaimAsync(
            "project-a", teamId, conversationId, messageTs, null,
            connectionId, [connectionId, "connection-b"]);
        var retryBeforeDelivery = await store.TryClaimAsync(
            "project-a", teamId, conversationId, messageTs, null,
            connectionId, [connectionId, "connection-b"]);
        Assert.True(retryBeforeDelivery.Claimed);

        await using (var db = database.CreateContext())
        {
            db.SlackOutboxRows.Add(new SlackOutboxRow
            {
                Id = "outbox-a",
                ProjectId = "project-a",
                ConnectionId = connectionId,
                WorkspaceTeamId = teamId,
                ConversationId = conversationId,
                Kind = SlackOutboxKinds.UserAction,
                State = SlackOutboxStates.Pending,
                DispatchRef = SlackAmbiguousPromptStore.PromptDispatchRef(teamId, conversationId, messageTs),
                PayloadJson = "{}",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync();
        }

        var retryAfterDelivery = await store.TryClaimAsync(
            "project-a", teamId, conversationId, messageTs, null,
            connectionId, [connectionId, "connection-b"]);
        Assert.False(retryAfterDelivery.Claimed);
    }

    [Fact]
    public async Task Claim_persists_original_facts_and_complete_cross_project_candidates()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var store = NewStore(database);
        var candidates = new[]
        {
            new SlackSelectionCandidateReference("project-a", "connection-a"),
            new SlackSelectionCandidateReference("project-b", "connection-b"),
        };

        var result = await store.TryClaimAsync(
            "project-a", "team-facts", "channel-facts", "4.001", "4.000",
            "connection-a", candidates, "user-original", "do the task",
            "[{\"id\":\"file-1\"}]", SlackAmbiguityKinds.ThreadMultiMention);

        Assert.True(result.Claimed);
        Assert.Equal("user-original", result.Snapshot.SenderSlackUserId);
        Assert.Equal("do the task", result.Snapshot.TaskText);
        Assert.Equal("[{\"id\":\"file-1\"}]", result.Snapshot.FilesJson);
        Assert.Equal(SlackAmbiguityKinds.ThreadMultiMention, result.Snapshot.AmbiguityKind);
        Assert.Equal(
            candidates,
            JSON.Deserialize<List<SlackSelectionCandidateReference>>(result.Snapshot.CandidateReferencesJson));

        var lookup = await store.FindAsync("team-facts", "channel-facts", "4.001");
        Assert.NotNull(lookup);
        Assert.Equal("project-b", JSON.Deserialize<List<SlackSelectionCandidateReference>>(
            lookup!.CandidateReferencesJson)![1].ProjectId);
    }

    [Fact]
    public async Task Claim_rejects_incomplete_facts_or_candidate_references()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var store = NewStore(database);
        var candidates = new[]
        {
            new SlackSelectionCandidateReference("project-a", "connection-a"),
            new SlackSelectionCandidateReference("", "connection-b"),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryClaimAsync(
            "project-a", "team-invalid", "channel-invalid", "5.001", null,
            "connection-a", candidates, "user-original", "task", "[]",
            SlackAmbiguityKinds.RootMultiMention));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryClaimAsync(
            "project-a", "team-invalid", "channel-invalid", "5.002", null,
            "connection-a", new[]
            {
                new SlackSelectionCandidateReference("project-a", "connection-a"),
                new SlackSelectionCandidateReference("project-b", "connection-b"),
            }, "", "task", "[]", SlackAmbiguityKinds.RootMultiMention));
    }

    [Fact]
    public async Task Selection_decision_is_a_pending_only_compare_and_swap_with_preallocated_ids()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var store = NewStore(database);
        await store.TryClaimAsync(
            "project-a", "team-cas", "channel-cas", "6.001", "6.000",
            "connection-a", new[]
            {
                new SlackSelectionCandidateReference("project-a", "connection-a"),
                new SlackSelectionCandidateReference("project-b", "connection-b"),
            }, "user-original", "task", "[]", SlackAmbiguityKinds.RootMultiMention);

        var first = await store.TryDecideAsync(
            "team-cas", "channel-cas", "6.001", "project-b", "connection-b",
            SlackSelectionDispatchKinds.RootLaunch, "session-b", "input-b", "turn-b");
        var loser = await store.TryDecideAsync(
            "team-cas", "channel-cas", "6.001", "project-a", "connection-a",
            SlackSelectionDispatchKinds.RootLaunch, "session-a", "input-a", "turn-a");

        Assert.True(first.Decided);
        Assert.False(loser.Decided);
        Assert.Equal(SlackSelectionStates.Decided, loser.Snapshot.SelectionState);
        Assert.Equal("project-b", loser.Snapshot.ChosenProjectId);
        Assert.Equal("connection-b", loser.Snapshot.ChosenConnectionId);
        Assert.Equal("session-b", loser.Snapshot.SelectionSessionId);
        Assert.Equal("input-b", loser.Snapshot.SelectionInputId);
        Assert.Equal("turn-b", loser.Snapshot.SelectionTurnId);
    }

    [Fact]
    public async Task Equal_channel_and_message_identities_remain_isolated_by_workspace()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var store = NewStore(database);

        var workspaceA = await store.TryClaimAsync(
            "project-a", "team-a", "shared-channel", "3.001", null,
            "connection-a", ["connection-a", "connection-b"]);
        var workspaceB = await store.TryClaimAsync(
            "project-a", "team-b", "shared-channel", "3.001", null,
            "connection-b", ["connection-a", "connection-b"]);

        Assert.True(workspaceA.Claimed);
        Assert.True(workspaceB.Claimed);
        Assert.NotEqual(workspaceA.RowId, workspaceB.RowId);
    }

    private static SlackAmbiguousPromptStore NewStore(TestSqliteDatabase database) =>
        new(new TestDbContextFactory(database.Options), new FakeTimeProvider(Now));
}
