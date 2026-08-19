using Mohist.Server.Infrastructure.PublicApi;
using Xunit;

namespace Mohist.Server.UnitTests.Infrastructure.PublicApi;

public sealed class PublicProjectionNudgeTests
{
    [Fact]
    public async Task Nudge_CoalescesRepeatedSignals()
    {
        var nudge = new PublicProjectionNudge();
        nudge.Nudge();
        nudge.Nudge();
        nudge.Nudge();

        Assert.True(await nudge.WaitAsync(CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await nudge.WaitAsync(cancelled.Token));
    }
}
