using System.Net;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public sealed class RunnerRecoveryReportingSpecs
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WaitForRecovery_StopsAtBoundAndReportsOutstandingWorkUnresolved()
    {
        var time = new FakeTimeProvider(Start);
        var handler = RecoveryStatusHandler(Statuses("unresolved", "unresolved"));
        var verifier = BuildVerifier(handler, time, timeout: TimeSpan.FromSeconds(2));
        var interruption = Interrupt(
            new RunnerUpdateWorkIdentity("workflow", "run-1", "work-1", "task-1", "task"),
            new RunnerUpdateWorkIdentity("agent-job", "job-1", "job-work-1", null, "agent-job"));

        var wait = verifier.WaitForRecoveryAsync(interruption);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(2));

        var report = await wait;
        Assert.Equal(1, report.ExitCode);
        Assert.False(report.FullyRecovered);
        Assert.Equal(2, report.Works.Count);
        Assert.All(report.Works, work => Assert.Equal("unresolved", work.Status));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WaitForRecovery_DoesNotOutliveBoundWhenHttpIgnoresCancellation()
    {
        var time = new FakeTimeProvider(Start);
        var handler = new HangingRecoveryStatusHandler();
        var verifier = BuildVerifier(handler, time, timeout: TimeSpan.FromSeconds(2));
        var interruption = Interrupt(new RunnerUpdateWorkIdentity(
            "agent-job", "job-1", "job-work-1", null, "agent-job"));

        var wait = verifier.WaitForRecoveryAsync(interruption);
        await handler.RequestStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        var report = await wait.WaitAsync(TimeSpan.FromSeconds(1));
        handler.Complete();

        Assert.Equal(1, report.ExitCode);
        Assert.Equal("unresolved", Assert.Single(report.Works).Status);
    }

    [Fact]
    public async Task WaitForRecovery_ListsMixedRecoveredAndUnresolvedWithIdentityAndState()
    {
        var time = new FakeTimeProvider(Start);
        var handler = RecoveryStatusHandler(Statuses("receipt-acked", "unresolved"));
        var verifier = BuildVerifier(handler, time, timeout: TimeSpan.FromSeconds(1));
        var interruption = Interrupt(
            new RunnerUpdateWorkIdentity("workflow", "run-1", "work-1", "task-1", "task"),
            new RunnerUpdateWorkIdentity("agent-job", "job-1", "job-work-1", null, "agent-job"));

        var wait = verifier.WaitForRecoveryAsync(interruption);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        var report = await wait;

        using var output = new StringWriter();
        using var error = new StringWriter();
        report.WriteSummary(output, error);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains("workId=work-1", output.ToString());
        Assert.Contains("status=recovered", output.ToString());
        Assert.Contains("taskRunId=task-1", output.ToString());
        Assert.Contains("workId=job-work-1", error.ToString());
        Assert.Contains("status=unresolved", error.ToString());
    }

    [Fact]
    public async Task WaitForRecovery_AllAcknowledgedHasSuccessfulExitAndReportsBothRecoveryStates()
    {
        var time = new FakeTimeProvider(Start);
        var handler = RecoveryStatusHandler(Statuses("receipt-acked", "replacement-settled"));
        var verifier = BuildVerifier(handler, time, timeout: TimeSpan.FromSeconds(1));
        var interruption = Interrupt(
            new RunnerUpdateWorkIdentity("workflow", "run-1", "work-1", "task-1", "task"),
            new RunnerUpdateWorkIdentity("agent-job", "job-1", "job-work-1", null, "agent-job"));

        var report = await verifier.WaitForRecoveryAsync(interruption);

        Assert.Equal(0, report.ExitCode);
        Assert.True(report.FullyRecovered);
        Assert.Equal(["receipt-acked", "replacement-settled"], report.Works.Select(work => work.State));
    }

    [Fact]
    public async Task WaitForRecovery_ZeroAffectedWorkDoesNotClaimRecoveryOrPoll()
    {
        var time = new FakeTimeProvider(Start);
        var handler = RecoveryStatusHandler([]);
        var verifier = BuildVerifier(handler, time, timeout: TimeSpan.FromSeconds(1));

        var report = await verifier.WaitForRecoveryAsync(new RunnerInterruptResult(
            "runner-1",
            "interrupted",
            "interrupt-1",
            [],
            0,
            null,
            "operation-1",
            Start,
            []));

        using var output = new StringWriter();
        using var error = new StringWriter();
        report.WriteSummary(output, error);

        Assert.Empty(report.Works);
        Assert.Equal(0, report.ExitCode);
        Assert.Contains("affected work=none; no recovery claimed", output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WaitForRecovery_OldRunnerLossBeforeReceiptRemainsUnresolved()
    {
        var time = new FakeTimeProvider(Start);
        var handler = RecoveryStatusHandler(Statuses("unresolved"));
        var verifier = BuildVerifier(handler, time, timeout: TimeSpan.FromSeconds(1));
        var interruption = Interrupt(new RunnerUpdateWorkIdentity(
            "agent-job", "job-1", "job-work-1", null, "agent-job"));

        var wait = verifier.WaitForRecoveryAsync(interruption);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        var report = await wait;

        Assert.False(report.FullyRecovered);
        Assert.Equal("unresolved", Assert.Single(report.Works).Status);
        Assert.Contains("receipt-pending", Assert.Single(report.Works).State);
    }

    private static RunnerRefreshVerifier BuildVerifier(
        HttpMessageHandler handler,
        TimeProvider time,
        TimeSpan timeout) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") },
            new UpdateTestFactory().Commands,
            new FakeFileSystem(),
            getLocalHostname: () => "test-host",
            timeProvider: time,
            runnerRecoveryTimeout: timeout,
            runnerRecoveryPollInterval: TimeSpan.FromMilliseconds(100));

    private static RunnerInterruptResult Interrupt(params RunnerUpdateWorkIdentity[] works) =>
        new(
            "runner-1",
            "interrupted",
            "interrupt-1",
            works.Select(work => work.WorkId).ToArray(),
            works.Length,
            null,
            "runner-update:1",
            Start,
            works);

    private static RecordingHttpHandler RecoveryStatusHandler(IReadOnlyList<string> statuses)
    {
        return new RecordingHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/recovery-status", request.RequestUri!.AbsolutePath);
            var works = statuses
                .Select((status, index) => new
                {
                    ownerKind = index == 0 ? "workflow" : "agent-job",
                    ownerId = index == 0 ? "run-1" : "job-1",
                    workId = index == 0 ? "work-1" : "job-work-1",
                    taskRunId = index == 0 ? "task-1" : null,
                    workType = index == 0 ? "task" : "agent-job",
                    status,
                    acknowledged = status != "unresolved",
                })
                .ToArray();
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    operationId = "runner-update:1",
                    runnerId = "runner-1",
                    operationStatus = statuses.All(status => status != "unresolved") ? "settled" : "pending",
                    complete = statuses.All(status => status != "unresolved"),
                    affectedWorks = works,
                },
            }));
        });
    }

    private static IReadOnlyList<string> Statuses(params string[] statuses) => statuses;

    private sealed class HangingRecoveryStatusHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult(true);
            return _response.Task;
        }
    }
}
