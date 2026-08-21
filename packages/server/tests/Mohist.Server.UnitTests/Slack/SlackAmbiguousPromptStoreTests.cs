using Microsoft.Extensions.Time.Testing;
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
