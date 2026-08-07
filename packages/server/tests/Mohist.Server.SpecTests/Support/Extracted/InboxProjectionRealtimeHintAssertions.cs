using Mohist.Server.TestSupport;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Support;

internal static class InboxProjectionRealtimeHintAssertions
{
    public static async Task AssertPersistedCountsAsync(
        TestSqliteDatabase database,
        int inbox,
        int hints)
    {
        await using var db = database.CreateContext();
        Assert.Equal(inbox, await db.InboxItems.CountAsync(item => item.ProjectId == "proj_atomic"));
        Assert.Equal(hints, await db.WorkflowRunEvents.CountAsync(evt =>
            evt.Source == "/mohist/inbox" && evt.Type == "com.mohist.inbox.item-persisted"));
    }
}
