using Mohist.Server.Infrastructure;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentJobGrainSpecs : AgentJobGrainTestSupport
{
    public AgentJobGrainSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task SubmitAsync_TransitionsPendingToRunning_WhenRunnerAcceptsDispatch()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-dispatch-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-dispatch-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "Hello world",
            Model: "test/model",
            WorkspacePath: "/tmp/agent-job-test",
            ProjectId: projectId));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CurrentWorkId));
        Assert.Null(snapshot.FailureReason);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        Assert.Contains(jobKey, state.ActiveWorks.Select(w => w.OwnerId));
    }

    [Fact]
    public async Task ReportResultAsync_TransitionsRunningToCompleted_OnSuccess()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-success-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-success-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("do the thing", projectId, "/tmp/agent-job-success"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var snapshot = await job.GetRuntimeSnapshotAsync();
        var workId = snapshot.CurrentWorkId!;
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var report = await runner.ReportAgentJobResultAsync(
            jobKey,
            workId,
            new WorkResult("completed", "ok", Output: JSON.DeserializeElement("{}"), ExitCode: 0, ArtifactUploadIds: ["artifact-1"]));
        Assert.True(report.Tracked);

        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, terminal.Status);
        Assert.Equal("ok", terminal.Message);
        Assert.Equal("{}", terminal.Output);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Equal(new[] { "artifact-1" }, terminal.ArtifactUploadIds);
        Assert.Null(terminal.FailureReason);
    }

    [Fact]
    public async Task ReportResultAsync_TransitionsRunningToFailed_OnFailure()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-fail-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-fail-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("do a failing thing", projectId, "/tmp/agent-job-fail"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var snapshot = await job.GetRuntimeSnapshotAsync();
        var workId = snapshot.CurrentWorkId!;
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var report = await runner.ReportAgentJobResultAsync(
            jobKey,
            workId,
            new WorkResult("failed", "boom", Output: JSON.DeserializeElement("{\"error\":\"x\"}"), ExitCode: 1));
        Assert.True(report.Tracked);

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal("boom", terminal.Message);
        Assert.Equal("{\"error\":\"x\"}", terminal.Output);
        Assert.Equal(1, terminal.ExitCode);
        Assert.Equal("boom", terminal.FailureReason);
    }

    [Fact]
    public async Task ReportResultAsync_AfterTerminalCompletion_IsRejected_AndPriorResultPreserved()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-terminal-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-terminal-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("first", projectId, "/tmp/agent-job-terminal"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.ReportAgentJobResultAsync(jobKey, workId, new WorkResult("completed", "first result"));
        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var firstTerminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, firstTerminal.Status);
        Assert.Equal("first result", firstTerminal.Message);

        await runner.ReportAgentJobResultAsync(
            jobKey,
            workId,
            new WorkResult("failed", "second result"));

        var stillTerminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, stillTerminal.Status);
        Assert.Equal("first result", stillTerminal.Message);
    }

    [Fact]
    public async Task SubmitAsync_NoEligibleRunner_StaysPendingAndRetriesWithBackoff()
    {
        await ClearGlobalRunnerRegistryAsync();

        var jobKey = $"agent-job-no-slot-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("no slot", $"agent-job-missing-project-{Guid.NewGuid():N}", "/tmp/agent-job-no-slot"));

        var stillPending = await WaitForAsync(
            () => job.GetStatusAsync(),
            s => s == AgentJobStatus.Pending,
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(25),
            "job stays pending past first attempt");
        Assert.Equal(AgentJobStatus.Pending, stillPending);
    }

    [Fact]
    public async Task SubmitAsync_RunnerAtPersistedSlots_LeavesSecondAgentJobPending()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-capacity-runner-{Guid.NewGuid():N}");
        var firstJobKey = $"agent-job-capacity-first-{Guid.NewGuid():N}";
        var secondJobKey = $"agent-job-capacity-second-{Guid.NewGuid():N}";
        var firstJob = JobGrain(firstJobKey);
        var secondJob = JobGrain(secondJobKey);

        await firstJob.SubmitAsync(MakeInput("occupy slot", projectId, "/tmp/agent-job-capacity-first"));
        await WaitForStatusAsync(firstJob, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await secondJob.SubmitAsync(MakeInput("wait for slot", projectId, "/tmp/agent-job-capacity-second"));

        var secondStatus = await WaitForAsync(
            () => secondJob.GetStatusAsync(),
            status => status == AgentJobStatus.Pending,
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(25),
            "second job remains pending while runner slot is occupied");
        Assert.Equal(AgentJobStatus.Pending, secondStatus);

        var secondSnapshot = await secondJob.GetRuntimeSnapshotAsync();
        Assert.Null(secondSnapshot.RunnerId);
        Assert.Null(secondSnapshot.CurrentWorkId);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var activeWorks = (await runner.GetRuntimeStateAsync()).ActiveWorks;
        Assert.Single(activeWorks, w => w.OwnerKind == WorkDispatchOwnerKinds.AgentJob);
        Assert.Contains(activeWorks, w => w.OwnerId == firstJobKey);
        Assert.DoesNotContain(activeWorks, w => w.OwnerId == secondJobKey);
    }

    [Fact]
    public async Task SubmitAsync_BoundExceeded_TransitionsToFailedWithRunnerUnavailable()
    {
        await ClearGlobalRunnerRegistryAsync();

        var jobKey = $"agent-job-bound-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("no runner ever", $"agent-job-missing-project-bound-{Guid.NewGuid():N}", "/tmp/agent-job-bound"));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(6));
        await job.CheckTimeoutsAsync();

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, terminal.FailureReason);
    }

    [Fact]
    public async Task SubmitAsync_GenericSession_NoEligibleRunner_ClosesSessionAsFailed()
    {
        await ClearGlobalRunnerRegistryAsync();

        var projectId = $"agent-job-missing-project-bound-{Guid.NewGuid():N}";
        var sessionId = $"generic-session-{Guid.NewGuid():N}";
        var sessionGrain = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await sessionGrain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/generic-session",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));

        var jobKey = $"agent-job-bound-generic-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "no runner ever",
            WorkspacePath: "/tmp/agent-job-bound",
            ProjectId: projectId,
            AgentSessionId: sessionId));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(6));
        await job.CheckTimeoutsAsync();

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, terminal.FailureReason);
        await sessionGrain.FlushForTestAsync();

        var closedPayload = await WaitForAsync(
            async () =>
            {
                await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
                var turnIds = await db.AgentSessionTranscriptTurns
                    .Where(t => t.SessionId == sessionId)
                    .Select(t => t.Id)
                    .ToListAsync();
                if (turnIds.Count == 0) return null;
                return await db.AgentSessionTranscriptParts
                    .Where(p => turnIds.Contains(p.TurnId) && p.Type == TranscriptPartTypes.SessionActivity)
                    .Select(p => p.PayloadJson)
                    .FirstOrDefaultAsync();
            },
            payload => !string.IsNullOrWhiteSpace(payload),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(50),
            "generic session terminal activity transcript event")!;
        using var payload = JsonDocument.Parse(closedPayload!);
        // Issue 484: terminal delivery now writes a session.activity
        // (activity=idle) part. The work result status remains on the
        // payload; the failureCategory is the AgentJob's own verdict
        // (asserted above via terminal.FailureReason) and is no longer
        // mirrored onto the session transcript.
        Assert.Equal("failed", payload.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SubmitAsync_DoesNotUseWorkflowAssignment()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-bypass-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-bypass-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("should bypass backlog", projectId, "/tmp/agent-job-bypass"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled.OwnerKind);
        Assert.Equal(jobKey, polled.AgentJobId);
    }

    [Fact]
    public async Task RunningJob_WithoutReport_JobTimeout_TransitionsToFailed()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-timeout-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-timeout-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("never reports", projectId, "/tmp/agent-job-timeout"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await job.CheckTimeoutsAsync();

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.ReportTimeout, terminal.FailureReason);
    }

    [Fact]
    public async Task DelayedGenericJobFailure_AfterReset_DoesNotCloseTheReplacementRuntime()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-reset-project-{Guid.NewGuid():N}";
        var sessionId = $"agent-job-reset-session-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-a",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-reset",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-a"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(10));

        var job = JobGrain($"agent-job-reset-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput("delayed failure", ProjectId: projectId, AgentSessionId: sessionId));
        await job.AssignRunnerAsync("runner-a", "work-a");
        Assert.True(await job.RecordRuntimeSessionBindingAsync("runner-a", "work-a", sessionId, "runtime-a"));
        // Repeat of the same runtimeSessionId is idempotent — the
        // runner may re-report the binding after a reconnect, and
        // the grain must accept it as a no-op (no state mutation, no
        // lineage append). Mismatched values on a repeat report are
        // rejected (#410 T-001 AC).
        Assert.True(await job.RecordRuntimeSessionBindingAsync("runner-a", "work-a", sessionId, "runtime-a"));
        Assert.False(await job.RecordRuntimeSessionBindingAsync("runner-a", "work-a", sessionId, "runtime-b"));

        await session.ResetAsync(new ResetAgentSessionCommand("runtime-a", "runtime-b"));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await job.CheckTimeoutsAsync();
        await session.FlushForTestAsync();

        Assert.Equal("runtime-b", (await session.GetAsync())?.AgentSessionId);
        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var turnIds = await db.AgentSessionTranscriptTurns
            .Where(turn => turn.SessionId == sessionId)
            .Select(turn => turn.Id)
            .ToListAsync();
        Assert.Empty(await db.AgentSessionTranscriptParts
            .Where(part => turnIds.Contains(part.TurnId) && part.Type == TranscriptPartTypes.SessionClosed)
            .ToListAsync());
    }

    [Fact]
    public async Task AttachedGenericJobFailure_RecordsOneTerminalFactWithRuntimeFailureCategory()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-close-project-{Guid.NewGuid():N}";
        var sessionId = $"agent-job-close-session-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-a",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-close",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-a"));

        var job = JobGrain($"agent-job-close-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput("record terminal failure", ProjectId: projectId, AgentSessionId: sessionId));
        await job.AssignRunnerAsync("runner-a", "work-a");
        Assert.True(await job.RecordRuntimeSessionBindingAsync("runner-a", "work-a", sessionId, "runtime-a"));

        await job.ReportResultAsync("runner-a", "work-a", new WorkResult(
            "failed",
            "prompt timed out",
            Output: JSON.DeserializeElement("""{"failureCategory":"prompt_timeout"}"""),
            ExitCode: 1));
        await session.FlushForTestAsync();

        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var turnIds = await db.AgentSessionTranscriptTurns
            .Where(turn => turn.SessionId == sessionId)
            .Select(turn => turn.Id)
            .ToListAsync();
        var closed = Assert.Single(await db.AgentSessionTranscriptParts
            .Where(part => turnIds.Contains(part.TurnId) && part.Type == TranscriptPartTypes.SessionActivity)
            .ToListAsync());

        Assert.Equal(1, closed.RawEventCount);
        using var payload = JsonDocument.Parse(closed.PayloadJson);
        // Issue 484: terminal delivery now writes exactly one
        // session.activity (activity=idle) part. The runtime failure
        // category is the AgentJob's own verdict and is no longer
        // mirrored onto the session transcript; the work result status
        // remains observable on the part payload.
        Assert.Equal("failed", payload.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetGrain_IAgentJobGrain_ResolvesActiveActivation()
    {
        var jobKey = $"agent-job-resolve-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);

        var status = await job.GetStatusAsync();
        Assert.Equal(AgentJobStatus.Pending, status);

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, snapshot.Status);
        Assert.Null(snapshot.RunnerId);
        Assert.Null(snapshot.CurrentWorkId);

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Pending, terminal.Status);
        Assert.Null(terminal.FailureReason);
    }

    [Fact]
    public async Task SubmitAsync_SecondCallAfterRunning_ThrowsInvalidOperationException()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-resubmit-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-resubmit-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("first", projectId, "/tmp/agent-job-resubmit"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await job.SubmitAsync(MakeInput("second", projectId, "/tmp/agent-job-resubmit")));
    }

    [Fact]
    public async Task SubmitAsync_IdenticalSecondCallAfterRunning_IsIdempotent()
    {
        var (_, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-idempotent-runner-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-idempotent-{Guid.NewGuid():N}");
        var input = MakeInput("same", projectId, "/tmp/agent-job-idempotent");

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await job.SubmitAsync(input with { });

        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }

    [Fact]
    public async Task SubmitAsync_NoEligibleRunner_IncrementsDispatchAttemptsAcrossRetries()
    {
        await ClearGlobalRunnerRegistryAsync();

        var jobKey = $"agent-job-retry-attempts-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("retry attempts", $"missing-project-{Guid.NewGuid():N}", "/tmp/agent-job-retry"));

        _fixture.TimeProvider.Advance(TimeSpan.FromMilliseconds(75));
        await job.CheckTimeoutsAsync();
        var snapshot = await job.GetRuntimeSnapshotAsync();

        Assert.Equal(AgentJobStatus.Pending, snapshot.Status);
        Assert.True(snapshot.DispatchAttempts >= 2);
    }

}
