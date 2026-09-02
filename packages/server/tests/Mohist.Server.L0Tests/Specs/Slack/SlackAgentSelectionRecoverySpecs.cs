using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.TestSupport;
using Mohist.Server.L0Tests.Support;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Slack;

[Collection("MohistDb")]
public sealed class SlackAgentSelectionRecoverySpecs
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
    public async Task Concurrent_writers_have_exactly_one_durable_winner()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var left = NewStore(database);
        var right = NewStore(database);
        var candidates = new[]
        {
            new SlackSelectionCandidateReference("project-a", "connection-a"),
            new SlackSelectionCandidateReference("project-a", "connection-b"),
        };

        var results = await Task.WhenAll(
            left.TryClaimAsync(
                "project-a", "team-race", "channel-race", "1.101", "1.100",
                "connection-a", candidates, "user-race", "left", "[]",
                SlackAmbiguityKinds.RootMultiMention),
            right.TryClaimAsync(
                "project-a", "team-race", "channel-race", "1.101", "1.100",
                "connection-b", candidates, "user-race", "right", "[]",
                SlackAmbiguityKinds.RootMultiMention));

        Assert.Equal(2, results.Length);
        var winner = Assert.Single(results, result => result.Claimed);
        var loser = Assert.Single(results, result => !result.Claimed);
        Assert.Equal(winner.RowId, loser.RowId);
        Assert.Equal(winner.WinningConnectionId, loser.WinningConnectionId);
        Assert.Equal(winner.Snapshot.TaskText, loser.Snapshot.TaskText);
        Assert.Equal(
            winner.Snapshot,
            (await left.FindAsync("team-race", "channel-race", "1.101")));
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

        var candidates = new[]
        {
            new SlackSelectionCandidateReference("project-a", connectionId, "U_A"),
            new SlackSelectionCandidateReference("project-a", "connection-b", "U_B"),
        };
        await store.TryClaimAsync(
            "project-a", teamId, conversationId, messageTs, null,
            connectionId, candidates, "U_ACTOR", "do work", "[]",
            SlackAmbiguityKinds.RootMultiMention);
        var retryBeforeDelivery = await store.TryClaimAsync(
            "project-a", teamId, conversationId, messageTs, null,
            connectionId, candidates, "U_ACTOR", "do work", "[]",
            SlackAmbiguityKinds.RootMultiMention);
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
            connectionId, candidates, "U_ACTOR", "do work", "[]",
            SlackAmbiguityKinds.RootMultiMention);
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
    public async Task Selection_lifecycle_expires_pending_retries_decided_and_reaps_only_finished_rows()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var time = new FakeTimeProvider(Now);
        var store = new SlackAmbiguousPromptStore(
            new TestDbContextFactory(database.Options),
            time);

        var pending = await store.TryClaimAsync(
            "project-a", "team-life", "channel-life", "7.001", "7.000",
            "connection-a", ["connection-a", "connection-b"]);
        var decided = await store.TryClaimAsync(
            "project-a", "team-life", "channel-life", "7.002", "7.000",
            "connection-a", ["connection-a", "connection-b"]);
        await store.TryDecideAsync(
            "team-life", "channel-life", "7.002", "project-a", "connection-a",
            SlackSelectionDispatchKinds.RootLaunch, "session", "input", "turn");
        const int retryIntervalMinutes = 1;
        var retryInterval = TimeSpan.FromMinutes(retryIntervalMinutes);
        Assert.True(await store.TryBeginDispatchAsync(
            decided.RowId, time.GetUtcNow(), retryInterval));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(await store.TryBeginDispatchAsync(
            decided.RowId, time.GetUtcNow(), retryInterval));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await store.TryBeginDispatchAsync(
            decided.RowId, time.GetUtcNow(), retryInterval));
        Assert.Equal(2, (await store.FindAsync(
            "team-life", "channel-life", "7.002"))!.AttemptCount);
        Assert.True(await store.MarkCompletedAsync(decided.RowId, "accepted"));

        await using (var db = database.CreateContext())
        {
            var pendingRow = (await db.SlackAmbiguousPrompts
                .Where(row => row.Id == pending.RowId)
                .ToListAsync()).Single();
            pendingRow.PromptedAt = Now.AddMinutes(-6);
            pendingRow.UpdatedAt = Now.AddMinutes(-6);

            var completedRow = (await db.SlackAmbiguousPrompts
                .Where(row => row.Id == decided.RowId)
                .ToListAsync()).Single();
            completedRow.FinishedAt = Now.AddMinutes(-31);
            completedRow.UpdatedAt = Now.AddMinutes(-31);
            await db.SaveChangesAsync();
        }

        var pendingRows = await store.ListByStateAsync(
            "project-a", SlackSelectionStates.Pending, Now.AddMinutes(-5), CancellationToken.None);
        Assert.Single(pendingRows);
        Assert.Equal(pending.RowId, pendingRows[0].Id);

        Assert.True(await store.TrySettleAsync(
            pending.RowId, SlackSelectionStates.Pending, "expired"));
        await using (var db = database.CreateContext())
        {
            var expired = (await db.SlackAmbiguousPrompts
                .Where(row => row.Id == pending.RowId)
                .ToListAsync()).Single();
            expired.FinishedAt = Now.AddMinutes(-31);
            expired.UpdatedAt = Now.AddMinutes(-31);
            await db.SaveChangesAsync();
        }
        var removed = await store.DeleteFinishedBeforeAsync(Now.AddMinutes(-30));
        Assert.Equal(2, removed);
        Assert.Null(await store.FindAsync("team-life", "channel-life", "7.001"));
        Assert.Null(await store.FindAsync("team-life", "channel-life", "7.002"));
    }

    [Fact]
    public async Task Finished_queries_apply_state_and_finished_cutoffs_and_preserve_unmatched_rows()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var store = NewStore(database);
        var rows = new Dictionary<string, SlackAmbiguousPromptResult>(StringComparer.Ordinal);
        foreach (var messageTs in new[]
                 {
                     "8.001", "8.002", "8.003", "8.004", "8.005", "8.006",
                 })
        {
            rows[messageTs] = await store.TryClaimAsync(
                "project-a", "team-retain", "channel-retain", messageTs, null,
                "connection-a", ["connection-a", "connection-b"]);
        }

        await store.TryDecideAsync(
            "team-retain", "channel-retain", "8.002", "project-a", "connection-a",
            SlackSelectionDispatchKinds.RootLaunch, "session-decided", "input-decided", "turn-decided");
        await store.TryDecideAsync(
            "team-retain", "channel-retain", "8.003", "project-a", "connection-a",
            SlackSelectionDispatchKinds.RootLaunch, "session-old-completed", "input-old-completed", "turn-old-completed");
        Assert.True(await store.MarkCompletedAsync(rows["8.003"].RowId, "accepted"));
        Assert.True(await store.TrySettleAsync(
            rows["8.004"].RowId, SlackSelectionStates.Pending, "old-settled"));
        Assert.True(await store.TrySettleAsync(
            rows["8.005"].RowId, SlackSelectionStates.Pending, "recent-settled"));
        await store.TryDecideAsync(
            "team-retain", "channel-retain", "8.006", "project-a", "connection-a",
            SlackSelectionDispatchKinds.RootLaunch, "session-recent-completed", "input-recent-completed", "turn-recent-completed");
        Assert.True(await store.MarkCompletedAsync(rows["8.006"].RowId, "accepted"));

        await using (var db = database.CreateContext())
        {
            var finished = await db.SlackAmbiguousPrompts
                .Where(row => row.MessageTs == "8.003"
                    || row.MessageTs == "8.004"
                    || row.MessageTs == "8.005"
                    || row.MessageTs == "8.006")
                .ToListAsync();
            foreach (var row in finished)
            {
                row.FinishedAt = row.MessageTs is "8.003" or "8.004"
                    ? Now.AddMinutes(-31)
                    : Now.AddMinutes(-29);
            }
            await db.SaveChangesAsync();
        }

        var recentSettled = await store.ListSettledSinceAsync(Now.AddMinutes(-30));
        var listed = Assert.Single(recentSettled);
        Assert.Equal("8.005", listed.MessageTs);
        Assert.Equal("recent-settled", listed.SettleReason);

        var removed = await store.DeleteFinishedBeforeAsync(Now.AddMinutes(-30));
        Assert.Equal(2, removed);
        Assert.Null(await store.FindAsync("team-retain", "channel-retain", "8.003"));
        Assert.Null(await store.FindAsync("team-retain", "channel-retain", "8.004"));
        Assert.NotNull(await store.FindAsync("team-retain", "channel-retain", "8.001"));
        Assert.Equal(
            SlackSelectionStates.Decided,
            (await store.FindAsync("team-retain", "channel-retain", "8.002"))!.SelectionState);
        Assert.NotNull(await store.FindAsync("team-retain", "channel-retain", "8.005"));
        Assert.NotNull(await store.FindAsync("team-retain", "channel-retain", "8.006"));
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
