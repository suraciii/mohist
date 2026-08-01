using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public class WorkflowRunStatusCacheTests
{
    [Fact]
    public void StoreEvictsTheOldestEntryWhenCapacityIsReached()
    {
        var cache = new WorkflowRunStatusCache(capacity: 2);
        var first = CreateRun("first");
        var second = CreateRun("second");
        var third = CreateRun("third");

        cache.Store(first.Id, 1, first);
        cache.Store(second.Id, 1, second);
        cache.Store(third.Id, 1, third);

        Assert.False(cache.TryGet(first.Id, 1, out _));
        Assert.True(cache.TryGet(second.Id, 1, out var cachedSecond));
        Assert.Same(second, cachedSecond);
        Assert.True(cache.TryGet(third.Id, 1, out var cachedThird));
        Assert.Same(third, cachedThird);
        Assert.Equal(2, cache.Count);
    }

    private static WorkflowRun CreateRun(string id) => new()
    {
        Id = id,
        Metadata = new WorkflowRunMetadata(null, DateTimeOffset.UnixEpoch),
        Stages = [],
    };
}
