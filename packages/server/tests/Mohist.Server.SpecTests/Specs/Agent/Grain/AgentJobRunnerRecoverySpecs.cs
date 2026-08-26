using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Core.Internal;
using Xunit;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class AgentJobRunnerRecoverySpecs : AgentJobGrainTestSupport
{
    public AgentJobRunnerRecoverySpecs(AgentJobGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerRestart_RedeliversSameWork_AndTerminalReportIsIdempotent()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            "agent-job-recovery-runner",
            projectId: "agent-job-recovery-project");
        var jobKey = "agent-job-recovery";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("recover after runner restart", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var beforeRestart = await job.GetRuntimeSnapshotAsync();
        var originalWorkId = beforeRestart.CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var recovering = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Unknown, recovering.Status);
        Assert.True(recovering.IsRecovering);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, recovering.FailureReason);
        Assert.NotNull(recovering.RecoveryDeadlineAt);

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DispatchService>();
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            projectId));

        var redelivery = Assert.Single(
            (await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        Assert.Equal(jobKey, redelivery.AgentJobId);
        Assert.Equal(originalWorkId, redelivery.WorkId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, redelivery.OwnerKind);

        var repeated = Assert.Single(
            (await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        Assert.Equal(redelivery.AgentJobId, repeated.AgentJobId);
        Assert.Equal(redelivery.WorkId, repeated.WorkId);
        Assert.Equal(redelivery.OwnerKind, repeated.OwnerKind);
        Assert.Equal(redelivery.With, repeated.With);
        Assert.Equal(redelivery.Variables, repeated.Variables);

        var workKey = $"{WorkDispatchOwnerKinds.AgentJob}:{jobKey}:{originalWorkId}";
        Assert.Empty(
            (await dispatch.PollAsync(runnerId, new RunnerPollRequest([workKey], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        var firstReport = await job.ReportResultAsync(
            runnerId,
            originalWorkId,
            new WorkResult("completed", "recovered"));
        var duplicateReport = await job.ReportResultAsync(
            runnerId,
            originalWorkId,
            new WorkResult("failed", "stale duplicate"));

        Assert.True(firstReport.Accepted);
        Assert.False(duplicateReport.Accepted);
        Assert.Equal("refused", duplicateReport.Reason);
        Assert.Equal(AgentJobStatus.Completed, (await job.GetTerminalResultAsync()).Status);
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        await runner.UnregisterAsync();
    }

    [Fact]
    public async Task RunnerRestart_StartedFenceObservation_EntersUnknownWithoutTerminalFailure()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            "agent-job-recovery-unknown-runner",
            projectId: "agent-job-recovery-unknown-project");
        var jobKey = "agent-job-recovery-unknown";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("preserve unknown after runner restart", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();
        var recovering = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Unknown, recovering.Status);
        Assert.True(recovering.IsRecovering);

        var unknown = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult(
                "unknown",
                "Runner restarted after a durable started fence without a completed result receipt."));

        Assert.True(unknown.Accepted);
        Assert.Equal("unknown", unknown.Reason);
        var unknownResult = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Unknown, unknownResult.Status);

        var duplicateUnknown = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("unknown", "same observation"));
        Assert.True(duplicateUnknown.Accepted);
        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());

        var terminal = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("completed", "authoritative result after reconciliation"));
        Assert.True(terminal.Accepted);
        Assert.Equal(AgentJobStatus.Completed, await job.GetStatusAsync());
        await runner.UnregisterAsync();
    }

    [Fact]
    public async Task RecoveringAgentJob_IsNotTakenOverByAnotherRunner()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-recovery-owner-{Guid.NewGuid():N}",
            projectId: $"agent-job-recovery-owner-project-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-recovery-owner-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("keep the original runner", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var original = await job.GetRuntimeSnapshotAsync();
        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        var otherRunnerId = $"agent-job-recovery-other-{Guid.NewGuid():N}";
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherRunnerId);
        await otherRunner.RegisterAsync(new RunnerInfo(
            otherRunnerId,
            ["spec/*"],
            "agent-job-other-host",
            projectId));

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DispatchService>();
        Assert.Empty((await dispatch.PollAsync(otherRunnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        var after = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(runnerId, after.RunnerId);
        Assert.Equal(original.CurrentWorkId, after.CurrentWorkId);
        Assert.True(after.IsRecovering);
        await otherRunner.UnregisterAsync();
    }

    [Fact]
    public async Task LateReportAtRecoveryDeadlineIsStaleAfterOwnerTerminalizesTheJob()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-recovery-late-report-runner-{Guid.NewGuid():N}",
            projectId: $"agent-job-recovery-late-report-project-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-recovery-late-report-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("late result loses deadline race", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        var recovering = await job.GetRuntimeSnapshotAsync();
        var deadline = Assert.IsType<DateTimeOffset>(recovering.RecoveryDeadlineAt);
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());

        var late = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("completed", "late previous-generation result"));
        Assert.False(late.Accepted);
        Assert.Equal("refused", late.Reason);

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, terminal.FailureReason);
        Assert.False((await job.GetRuntimeSnapshotAsync()).IsRecovering);

        var duplicate = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("completed", "duplicate late result"));
        Assert.False(duplicate.Accepted);
        Assert.Equal("refused", duplicate.Reason);
        Assert.Equal(AgentJobStatus.Failed, (await job.GetTerminalResultAsync()).Status);
    }

    [Fact]
    public async Task ReconciliationFailure_PublishesDistinctUnknownAndFailedTerminalDeliveries()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-retry-delivery-runner-{Guid.NewGuid():N}",
            projectId: $"agent-job-retry-delivery-project-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-retry-delivery-{Guid.NewGuid():N}";
        var sessionId = $"session-retry-delivery-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-fixture",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));

        var job = JobGrain(jobKey);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: sessionId,
            InputId: Guid.NewGuid().ToString("N"),
            TurnId: Guid.NewGuid().ToString("N"),
            Prompt: "retryable reconciliation failure",
            ProjectId: projectId,
            AgentId: "agent-test",
            ConnectionOrigin: new ConnectionLaunchOrigin(
                "conn-retry-delivery",
                "team-1",
                "U_OWNER",
                "C-retry-delivery",
                "1710000000.000001")));
        await job.SubmitPreparedLaunchAsync();
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        var recovering = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Unknown, recovering.Status);
        var unknownDelivery = _fixture.EventStore.Appended
            .Where(envelope => envelope.Envelope.Type == EventCatalog.ReverseDns.AgentJobTerminalDelivery)
            .ToList();
        Assert.Contains(unknownDelivery, envelope =>
            envelope.Envelope.Id == AgentJobSessionDeliveryIds.UnknownTerminalDeliveryEventId(jobKey)
            && envelope.Envelope.Data!.Value.GetProperty("status").GetString() == "unknown");

        var deadline = Assert.IsType<DateTimeOffset>(recovering.RecoveryDeadlineAt);
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await job.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, terminal.FailureReason);
        var failedDelivery = _fixture.EventStore.Appended
            .Where(envelope => envelope.Envelope.Type == EventCatalog.ReverseDns.AgentJobTerminalDelivery)
            .ToList();
        Assert.Contains(failedDelivery, envelope =>
            envelope.Envelope.Id == AgentJobSessionDeliveryIds.TerminalDeliveryEventId(jobKey)
            && envelope.Envelope.Data!.Value.GetProperty("status").GetString() == "failed");
        // The persisted-event dedup key is (source, id): the interim Unknown
        // delivery and the reconciled Failed delivery must be two events with
        // distinct ids, or the final failure payload would be silently
        // dropped in production.
        var deliveryIds = failedDelivery.Select(envelope => envelope.Envelope.Id).ToHashSet();
        Assert.Contains(AgentJobSessionDeliveryIds.UnknownTerminalDeliveryEventId(jobKey), deliveryIds);
        Assert.Contains(AgentJobSessionDeliveryIds.TerminalDeliveryEventId(jobKey), deliveryIds);
    }

    [Fact]
    public async Task ReconciliationFailure_ReplacesPendingUnknownDeliveryAfterInterimAppendFailure()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-retry-delivery-failure-runner-{Guid.NewGuid():N}",
            projectId: $"agent-job-retry-delivery-failure-project-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-retry-delivery-failure-{Guid.NewGuid():N}";
        var sessionId = $"session-retry-delivery-failure-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-fixture",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));

        var job = JobGrain(jobKey);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: sessionId,
            InputId: Guid.NewGuid().ToString("N"),
            TurnId: Guid.NewGuid().ToString("N"),
            Prompt: "retryable reconciliation failure with a pending unknown delivery",
            ProjectId: projectId,
            AgentId: "agent-test",
            ConnectionOrigin: new ConnectionLaunchOrigin(
                "conn-retry-delivery-failure",
                "team-1",
                "U_OWNER",
                "C-retry-delivery-failure",
                "1710000000.000001")));
        await job.SubmitPreparedLaunchAsync();
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        _fixture.EventStore.ThrowOnAppend = envelope =>
            envelope.Type == EventCatalog.ReverseDns.AgentJobTerminalDelivery
            && envelope.Id == AgentJobSessionDeliveryIds.UnknownTerminalDeliveryEventId(jobKey);
        try
        {
            await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
            var recovering = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Unknown, recovering.Status);
            Assert.DoesNotContain(
                _fixture.EventStore.Appended,
                envelope => envelope.Envelope.Id == AgentJobSessionDeliveryIds.UnknownTerminalDeliveryEventId(jobKey));

            var deadline = Assert.IsType<DateTimeOffset>(recovering.RecoveryDeadlineAt);
            _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
            _fixture.EventStore.ThrowOnAppend = null;
            await job.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);

            var terminal = await job.GetTerminalResultAsync();
            Assert.Equal(AgentJobStatus.Failed, terminal.Status);
            Assert.Equal(AgentJobFailureReasons.RunnerLost, terminal.FailureReason);

            var failedDelivery = Assert.Single(
                _fixture.EventStore.Appended,
                envelope => envelope.Envelope.Type == EventCatalog.ReverseDns.AgentJobTerminalDelivery
                    && envelope.Envelope.Id == AgentJobSessionDeliveryIds.TerminalDeliveryEventId(jobKey));
            Assert.Equal("failed", failedDelivery.Envelope.Data!.Value.GetProperty("status").GetString());
            Assert.Equal(
                AgentJobFailureReasons.RunnerLost,
                failedDelivery.Envelope.Data.Value.GetProperty("failureCategory").GetString());
        }
        finally
        {
            _fixture.EventStore.ThrowOnAppend = null;
        }
    }

    [Fact]
    public async Task RunnerLossRecoveryReminder_FailsAtPersistedDeadlineWithRecordedReason()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-recovery-deadline-runner-{Guid.NewGuid():N}",
            projectId: $"agent-job-recovery-deadline-project-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-recovery-deadline-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("fail after recovery deadline", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var recovering = await job.GetRuntimeSnapshotAsync();
        var deadline = Assert.IsType<DateTimeOffset>(recovering.RecoveryDeadlineAt);
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await job.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, terminal.FailureReason);
        Assert.False((await job.GetRuntimeSnapshotAsync()).IsRecovering);
    }

    [Fact]
    public async Task RunnerLossRecoveryReminder_ReDerivesDeadlineAfterJobReactivation()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-recovery-reactivation-runner-{Guid.NewGuid():N}",
            projectId: $"agent-job-recovery-reactivation-project-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-recovery-reactivation-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("re-arm after activation", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        var deadline = Assert.IsType<DateTimeOffset>((await job.GetRuntimeSnapshotAsync()).RecoveryDeadlineAt);

        await job.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = JobGrain(jobKey);
        Assert.True((await reactivated.GetRuntimeSnapshotAsync()).IsRecovering);

        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await reactivated.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);

        var terminal = await reactivated.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, terminal.FailureReason);
    }

    [Fact]
    public async Task RunnerRestart_PreservesEveryActiveAgentWorkWithoutPartialTerminalization()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            "agent-job-recovery-many-runner",
            projectId: "agent-job-recovery-project",
            maxWorkflowSlots: 2);
        var firstJob = JobGrain("agent-job-recovery-first");
        var secondJob = JobGrain("agent-job-recovery-second");

        await firstJob.SubmitAsync(MakeInput("recover first work", projectId));
        await secondJob.SubmitAsync(MakeInput("recover second work", projectId));
        await WaitForStatusAsync(firstJob, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        await WaitForStatusAsync(secondJob, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var firstWorkId = (await firstJob.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var secondWorkId = (await secondJob.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var firstRecovering = await firstJob.GetRuntimeSnapshotAsync();
        var secondRecovering = await secondJob.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Unknown, firstRecovering.Status);
        Assert.Equal(AgentJobStatus.Unknown, secondRecovering.Status);
        Assert.True(firstRecovering.IsRecovering);
        Assert.True(secondRecovering.IsRecovering);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            projectId));

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DispatchService>();
        var redeliveries = (await dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches;
        Assert.Equal(2, redeliveries.Count);
        Assert.Contains(redeliveries, work => work.AgentJobId == firstJob.GetPrimaryKeyString()
            && work.WorkId == firstWorkId);
        Assert.Contains(redeliveries, work => work.AgentJobId == secondJob.GetPrimaryKeyString()
            && work.WorkId == secondWorkId);

        var firstReport = await firstJob.ReportResultAsync(
            runnerId,
            firstWorkId,
            new WorkResult("completed", "first recovered work"));
        var secondReport = await secondJob.ReportResultAsync(
            runnerId,
            secondWorkId,
            new WorkResult("completed", "second recovered work"));

        Assert.True(firstReport.Accepted);
        Assert.True(secondReport.Accepted);
        Assert.Equal(AgentJobStatus.Completed, await firstJob.GetStatusAsync());
        Assert.Equal(AgentJobStatus.Completed, await secondJob.GetStatusAsync());
        await runner.UnregisterAsync();
    }
}
