using Microsoft.Extensions.Time.Testing;
using Mohist.Runner.Transport;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class RunnerHostSpecs
{
    [Fact]
    public async Task WorkAvailable_ExecutesAndReports()
    {
        using var cts = new CancellationTokenSource();
        var work = SpecHelpers.Work("task");
        var connection = new FakeConnection { Work = work, CancelOnReport = cts };
        var executor = new FakeExecutor(new WorkItemResult("completed", "ok"));
        var host = Host(connection, executor);

        await host.RunAsync(cts.Token);

        Assert.Equal(1, connection.ConnectCount);
        Assert.Equal(1, executor.ExecuteCount);
        Assert.Single(connection.Reports);
        Assert.Equal("completed", connection.Reports[0].Status);
        Assert.Equal(1, connection.DisconnectCount);
    }

    [Fact]
    public async Task NoWork_DoesNotReport()
    {
        using var cts = new CancellationTokenSource();
        var connection = new FakeConnection { CancelOnFirstEmptyPoll = cts };
        var executor = new FakeExecutor(new WorkItemResult("completed"));
        var host = Host(connection, executor);

        await host.RunAsync(cts.Token);

        Assert.Equal(1, connection.PollCount);
        Assert.Empty(connection.Reports);
        Assert.Equal(0, executor.ExecuteCount);
        Assert.Equal(1, connection.DisconnectCount);
    }

    [Fact]
    public async Task RunningRunner_SendsHeartbeatUsingTimeProvider()
    {
        using var cts = new CancellationTokenSource();
        var time = new FakeTimeProvider();
        var connection = new FakeConnection { BlockPolling = true };
        var executor = new FakeExecutor(new WorkItemResult("completed"));
        var host = Host(connection, executor, time, new RunnerHostOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            IdleDelay = TimeSpan.FromHours(1),
        });

        var runTask = host.RunAsync(cts.Token);
        await connection.Connected.Task;

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => connection.HeartbeatCount > 0);

        cts.Cancel();
        await runTask;

        Assert.True(connection.HeartbeatCount > 0);
        Assert.Equal(1, connection.DisconnectCount);
    }

    private static RunnerHost Host(
        IServerConnection connection,
        IWorkExecutor executor,
        TimeProvider? timeProvider = null,
        RunnerHostOptions? options = null) => new(
            connection,
            executor,
            SpecHelpers.Logger<RunnerHost>(),
            timeProvider ?? TimeProvider.System,
            options ?? new RunnerHostOptions { HeartbeatInterval = TimeSpan.FromHours(1), IdleDelay = TimeSpan.FromHours(1) });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Condition was not met");
    }

    private sealed class FakeExecutor : IWorkExecutor
    {
        private readonly WorkItemResult _result;
        public int ExecuteCount { get; private set; }

        public FakeExecutor(WorkItemResult result)
        {
            _result = result;
        }

        public Task<WorkItemResult> ExecuteAsync(WorkItem workItem, CancellationToken ct)
        {
            ExecuteCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeConnection : IServerConnection
    {
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkItem? Work { get; init; }
        public CancellationTokenSource? CancelOnReport { get; init; }
        public CancellationTokenSource? CancelOnFirstEmptyPoll { get; init; }
        public bool BlockPolling { get; init; }
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public int HeartbeatCount { get; private set; }
        public int PollCount { get; private set; }
        public List<WorkItemResult> Reports { get; } = [];
        private bool _workReturned;

        public Task ConnectAsync(CancellationToken ct)
        {
            ConnectCount++;
            Connected.TrySetResult();
            return Task.CompletedTask;
        }

        public Task HeartbeatAsync(CancellationToken ct)
        {
            HeartbeatCount++;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            DisconnectCount++;
            return Task.CompletedTask;
        }

        public async Task<WorkItem?> PollAsync(CancellationToken ct)
        {
            PollCount++;
            if (BlockPolling)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            }

            if (Work is not null && !_workReturned)
            {
                _workReturned = true;
                return Work;
            }

            CancelOnFirstEmptyPoll?.Cancel();
            return null;
        }

        public Task ReportAsync(WorkItem workItem, WorkItemResult result, CancellationToken ct)
        {
            Reports.Add(result);
            CancelOnReport?.Cancel();
            return Task.CompletedTask;
        }
    }
}
