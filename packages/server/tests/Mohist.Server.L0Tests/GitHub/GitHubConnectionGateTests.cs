using Mohist.Server.GitHub.Infrastructure;
using Xunit;

namespace Mohist.Server.L0Tests.GitHub;

public sealed class GitHubConnectionGateTests
{
    [Fact]
    public async Task EnterAsync_SerializesBodiesForTheSameConnection()
    {
        var gate = new GitHubConnectionGate();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlap = 0;
        var maxOverlap = 0;

        var first = gate.EnterAsync("ghconn_1", async _ =>
        {
            Interlocked.Increment(ref overlap);
            maxOverlap = Math.Max(maxOverlap, overlap);
            firstEntered.SetResult();
            await releaseFirst.Task;
            Interlocked.Decrement(ref overlap);
            return 1;
        });
        await firstEntered.Task;

        var secondStarted = false;
        var second = gate.EnterAsync("ghconn_1", _ =>
        {
            Interlocked.Increment(ref overlap);
            maxOverlap = Math.Max(maxOverlap, overlap);
            secondStarted = true;
            Interlocked.Decrement(ref overlap);
            return Task.FromResult(2);
        });

        Assert.False(second.IsCompleted);
        releaseFirst.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.True(secondStarted);
        Assert.Equal(1, maxOverlap);
    }

    [Fact]
    public async Task EnterAsync_RunsReentrantSameConnectionBodyInline()
    {
        var gate = new GitHubConnectionGate();
        var innerRan = false;

        await gate.EnterAsync("ghconn_1", async _ =>
        {
            await gate.EnterAsync("ghconn_1", _ =>
            {
                innerRan = true;
                return Task.CompletedTask;
            });
            Assert.True(innerRan);
        });
    }

    [Fact]
    public async Task EnterAsync_LetsDifferentConnectionsRunConcurrently()
    {
        var gate = new GitHubConnectionGate();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        var first = gate.EnterAsync("ghconn_1", async _ =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.SetResult();
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        });
        await firstEntered.Task;

        var second = gate.EnterAsync("ghconn_2", _ =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.SetResult();
            return Task.FromResult(2);
        });

        await bothEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseFirst.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
    }
}
