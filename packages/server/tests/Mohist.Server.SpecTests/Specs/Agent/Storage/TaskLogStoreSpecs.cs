using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Storage;

/// <summary>
/// Calculation specs for <see cref="TaskLogStore"/>, the store behind the
/// task-log upload (<c>POST /api/.../task-log</c>) and read
/// (<c>GET /api/.../logs</c>) endpoints. These assert the store's write/read
/// semantics (append with seq dedup, owner-kind isolation between
/// <c>workflow</c> and <c>agent-job</c>, cursor pagination in seq order,
/// empty page for unknown owner) without an HTTP round-trip. The route
/// contract (400 malformed/invalid/oversized, 404 unknown owner, one
/// empty-page shape) stays in <c>TaskLogRouteSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class TaskLogStoreSpecs
{
    private readonly MohistDbFixture _fixture;

    public TaskLogStoreSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private TaskLogStore CreateStore() => _fixture.Services.GetRequiredService<TaskLogStore>();

    private static DateTimeOffset Now() => new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TaskLogLine Line(long seq, string text = "line", string source = "action") =>
        new(seq, Now().AddSeconds(seq), source, text);

    [Fact]
    public async Task AppendAsync_StoresEntriesAndDeduplicatesBySeq()
    {
        var store = CreateStore();
        var owner = Unique("owner");
        var workId = Unique("work");

        await store.AppendAsync(
            TaskLogOwnershipKinds.Workflow,
            owner,
            workId,
            [Line(1, "first"), Line(2, "second")],
            truncated: false);

        var page = await store.QueryAsync(TaskLogOwnershipKinds.Workflow, owner, workId, afterSeq: null, limit: null);
        Assert.Equal(2, page.Lines.Count);
        Assert.Equal("first", page.Lines[0].Text);
        Assert.Equal("second", page.Lines[1].Text);
        Assert.Null(page.NextCursor);
        Assert.False(page.Truncated);

        // Re-appending seq 1 must not duplicate; only the new seq 3 persists.
        await store.AppendAsync(
            TaskLogOwnershipKinds.Workflow,
            owner,
            workId,
            [Line(1, "first again"), Line(3, "third")],
            truncated: true);

        var afterDedup = await store.QueryAsync(TaskLogOwnershipKinds.Workflow, owner, workId, afterSeq: null, limit: null);
        Assert.Equal(3, afterDedup.Lines.Count);
        Assert.True(afterDedup.Truncated);
    }

    [Fact]
    public async Task AppendAsync_AgentJobOwnerIsIsolatedFromWorkflowOwner()
    {
        var store = CreateStore();
        var agentJob = Unique("aj");
        var workflowRun = Unique("wr");
        var sharedWorkId = Unique("work");

        await store.AppendAsync(
            TaskLogOwnershipKinds.AgentJob,
            agentJob,
            sharedWorkId,
            [Line(1, "agent-job line")],
            truncated: false);

        var agentPage = await store.QueryAsync(TaskLogOwnershipKinds.AgentJob, agentJob, sharedWorkId, afterSeq: null, limit: null);
        Assert.Single(agentPage.Lines);
        Assert.Equal("agent-job line", agentPage.Lines[0].Text);

        // Same work id under the workflow owner kind must not collide.
        var workflowPage = await store.QueryAsync(TaskLogOwnershipKinds.Workflow, workflowRun, sharedWorkId, afterSeq: null, limit: null);
        Assert.Empty(workflowPage.Lines);
    }

    [Fact]
    public async Task QueryAsync_ReturnsPagesInAscendingSeqOrderWithCursor()
    {
        var store = CreateStore();
        var owner = Unique("owner");
        var workId = Unique("work");

        await store.AppendAsync(
            TaskLogOwnershipKinds.Workflow,
            owner,
            workId,
            Enumerable.Range(1, 5).Select(seq => Line(seq, $"line {seq}")).ToList(),
            truncated: false);

        var first = await store.QueryAsync(TaskLogOwnershipKinds.Workflow, owner, workId, afterSeq: null, limit: 2);
        Assert.Equal(2, first.Lines.Count);
        Assert.Equal(1, first.Lines[0].Seq);
        Assert.Equal(2, first.Lines[1].Seq);
        Assert.Equal(2L, first.NextCursor);

        var second = await store.QueryAsync(TaskLogOwnershipKinds.Workflow, owner, workId, afterSeq: first.NextCursor, limit: 2);
        Assert.Equal(2, second.Lines.Count);
        Assert.Equal(3, second.Lines[0].Seq);
        Assert.Equal(4, second.Lines[1].Seq);
        Assert.Equal(4L, second.NextCursor);

        var final = await store.QueryAsync(TaskLogOwnershipKinds.Workflow, owner, workId, afterSeq: second.NextCursor, limit: 2);
        Assert.Single(final.Lines);
        Assert.Equal(5, final.Lines[0].Seq);
        Assert.Null(final.NextCursor);
        Assert.False(final.Truncated);
    }

    [Fact]
    public async Task QueryAsync_UnknownOwnerOrTask_ReturnsEmptyPage()
    {
        var store = CreateStore();

        var page = await store.QueryAsync(
            TaskLogOwnershipKinds.Workflow,
            Unique("missing-owner"),
            Unique("missing-work"),
            afterSeq: null,
            limit: null);

        Assert.Empty(page.Lines);
        Assert.Null(page.NextCursor);
        Assert.False(page.Truncated);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
