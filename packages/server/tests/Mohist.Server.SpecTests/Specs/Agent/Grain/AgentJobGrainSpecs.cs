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
public class AgentJobGrainSpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public AgentJobGrainSpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.DispatchObserver.Reset();
    }

    private IGrainFactory Grains => _fixture.Grains;

    private IAgentJobGrain JobGrain(string key) => Grains.GetGrain<IAgentJobGrain>(key);

    private static async Task<T> WaitForAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        TimeSpan step,
        string description)
        => await TestWait.ForAsync(probe, done, timeout, step, description);

    private static async Task WaitForStatusAsync(IAgentJobGrain job, AgentJobStatus expected, TimeSpan timeout)
    {
        await WaitForAsync(
            () => job.GetStatusAsync(),
            s => s == expected,
            timeout,
            TimeSpan.FromMilliseconds(25),
            $"status == {expected}",
            () => job.CheckTimeoutsAsync());
    }

    private static async Task<T> WaitForAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        TimeSpan step,
        string description,
        Func<Task> advance)
        => await TestWait.ForAsync(probe, done, timeout, step, description, advance);

    private async Task<(string RunnerId, string ProjectId)> RegisterAgentJobRunnerAsync(
        string runnerId,
        string? projectId = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        // Every agent-job spec shares the in-memory backlog directory and
        // global runner registry with the rest of the [Collection("RunnerGrain")]
        // cluster. Without a reset here, a stale runner from a prior spec
        // assigns this job before the new runner can, which makes the
        // assertions on snapshot.RunnerId non-deterministic. Clear both
        // before each registration.
        await ClearBacklogAsync();

        var pid = projectId ?? $"agent-job-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            pid));
        if (maxWorkflowSlots != RunnerCapacity.DefaultMaxWorkflowSlots)
        {
            await runner.UpdateAsync(maxWorkflowSlots);
        }
        return (runnerId, pid);
    }

    private async Task ClearBacklogAsync()
    {
        await ClearGlobalRunnerRegistryAsync();

        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);
    }

    private async Task ClearGlobalRunnerRegistryAsync()
    {
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var ids = await registry.ListRunnerIdsAsync();
        foreach (var id in ids)
            await registry.UnregisterAsync(id);
    }

    private static AgentJobInput MakeInput(string prompt, string projectId, string workspacePath = "/tmp/agent-job") =>
        new(Prompt: prompt, WorkspacePath: workspacePath, ProjectId: projectId);

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
            new WorkResult("completed", "ok", Output: "{}", ExitCode: 0, ArtifactUploadIds: ["artifact-1"]));
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
            new WorkResult("failed", "boom", Output: "{\"error\":\"x\"}", ExitCode: 1));
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
                    .Where(p => turnIds.Contains(p.TurnId) && p.Type == TranscriptPartTypes.SessionClosed)
                    .Select(p => p.PayloadJson)
                    .FirstOrDefaultAsync();
            },
            payload => !string.IsNullOrWhiteSpace(payload),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(50),
            "generic session terminal close transcript event")!;
        using var payload = JsonDocument.Parse(closedPayload!);
        Assert.Equal("failed", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, payload.RootElement.GetProperty("failureCategory").GetString());
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
            Output: """{"failureCategory":"prompt_timeout"}""",
            ExitCode: 1));
        await session.FlushForTestAsync();

        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var turnIds = await db.AgentSessionTranscriptTurns
            .Where(turn => turn.SessionId == sessionId)
            .Select(turn => turn.Id)
            .ToListAsync();
        var closed = Assert.Single(await db.AgentSessionTranscriptParts
            .Where(part => turnIds.Contains(part.TurnId) && part.Type == TranscriptPartTypes.SessionClosed)
            .ToListAsync());

        Assert.Equal(1, closed.RawEventCount);
        using var payload = JsonDocument.Parse(closed.PayloadJson);
        Assert.Equal("prompt_timeout", payload.RootElement.GetProperty("failureCategory").GetString());
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

    [Fact]
    public async Task SubmitAsync_WithAgentDefinition_ComposesInstructionsConfigAndPrompt_OnDispatchEnvelope()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-agent-source-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-agent-source-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"agent-session-{Guid.NewGuid():N}";

        var instructions = "Always respond in formal English; refuse non-code tasks.";
        var configElement = JsonDocument.Parse("{\"type\":\"opencode\",\"model\":\"openai/gpt-5.5\"}").RootElement.Clone();

        var input = new AgentJobInput(
            Prompt: "summarize the diff",
            Model: "openai/gpt-5.5",
            WorkspacePath: "/tmp/agent-job-agent-source",
            ProjectId: projectId,
            AgentId: "agent-7",
            AgentInstructions: instructions,
            AgentConfig: configElement,
            AgentSessionId: sessionId);

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled!.OwnerKind);
        Assert.Equal(jobKey, polled.AgentJobId);
        Assert.Equal(projectId, polled.ProjectId);
        Assert.Equal(sessionId, polled.AgentSessionId);

        Assert.False(string.IsNullOrWhiteSpace(polled.With));
        var with = JsonSerializer.Deserialize<JsonElement>(polled.With!);
        var promptValue = with.GetProperty("prompt");
        Assert.Equal(JsonValueKind.Object, promptValue.ValueKind);

        var agentLaunch = promptValue.GetProperty("agent-launch");
        Assert.Equal(instructions, agentLaunch.GetProperty("instructions").GetString());
        Assert.Equal("openai/gpt-5.5", agentLaunch.GetProperty("config").GetProperty("model").GetString());
        Assert.Equal("summarize the diff", agentLaunch.GetProperty("prompt").GetString());

        Assert.Equal("openai/gpt-5.5", with.GetProperty("model").GetString());
        Assert.Equal("openai/gpt-5.5", with.GetProperty("agent").GetProperty("model").GetString());
    }

    [Fact]
    public async Task SubmitAsync_RawPromptOnly_PassesBarePromptToDispatchEnvelope_AndLeavesNewFieldsUnset()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-raw-only-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-raw-only-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("raw prompt only", projectId, "/tmp/agent-job-raw-only"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled!.OwnerKind);
        Assert.Equal(jobKey, polled.AgentJobId);
        Assert.Equal(projectId, polled.ProjectId);
        Assert.Null(polled.AgentSessionId);

        var with = JsonSerializer.Deserialize<JsonElement>(polled.With!);
        Assert.Equal(JsonValueKind.String, with.GetProperty("prompt").ValueKind);
        Assert.Equal("raw prompt only", with.GetProperty("prompt").GetString());

        var variables = JsonSerializer.Deserialize<JsonElement>(polled.Variables!);
        Assert.Equal("/tmp/agent-job-raw-only", variables.GetProperty("workspace").GetProperty("path").GetString());
    }

    [Fact]
    public async Task SubmitAsync_AgentJobWithAgentSessionId_PopulatesSessionIdOnDispatchEnvelope()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-session-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-session-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"generic-session-{Guid.NewGuid():N}";

        var input = new AgentJobInput(
            Prompt: "ask the agent",
            WorkspacePath: "/tmp/agent-job-session",
            ProjectId: projectId,
            AgentId: "agent-42",
            AgentInstructions: "be brief",
            AgentSessionId: sessionId);

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(projectId, polled!.ProjectId);
        Assert.Equal(sessionId, polled.AgentSessionId);
    }

    [Fact]
    public async Task SubmitAsync_AgentJobWithoutSessionId_LeavesAgentSessionIdUnset()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-no-session-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-no-session-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        var input = new AgentJobInput(
            Prompt: "no session",
            WorkspacePath: "/tmp/agent-job-no-session",
            ProjectId: projectId);

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(projectId, polled!.ProjectId);
        Assert.Null(polled.AgentSessionId);
    }

    [Fact]
    public async Task SubmitAsync_PolledDispatch_ExposesProjectIdAndAgentSessionIdThroughHttpPoll()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-http-project-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-http-project-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"http-session-{Guid.NewGuid():N}";

        var input = new AgentJobInput(
            Prompt: "expose via http",
            WorkspacePath: "/tmp/agent-job-http-project",
            ProjectId: projectId,
            AgentId: "agent-http",
            AgentInstructions: "concise",
            AgentSessionId: sessionId);

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(projectId, polled!.ProjectId);
        Assert.Equal(sessionId, polled.AgentSessionId);
    }
}

[Collection("RunnerGrain")]
public class AgentJobOptionsBindingSpecs
{
    private readonly WorkflowGrainFixture _fixture;

    public AgentJobOptionsBindingSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AgentJobOptions_ResolveBackoffSchedule_UsesDefaultsFromConfigurationKnob()
    {
        var provider = _fixture.Cluster.GetSiloServiceProvider(null);
        var optionsAccessor = provider.GetRequiredService<IOptions<AgentJobOptions>>();
        var schedule = optionsAccessor.Value.ResolveBackoffSchedule();

        Assert.Equal(TimeSpan.FromMilliseconds(50), schedule.Initial);
        Assert.Equal(TimeSpan.FromMilliseconds(200), schedule.Cap);
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.TotalBound);
        Assert.Equal(TimeSpan.FromSeconds(10), optionsAccessor.Value.JobTimeout);

        var next = schedule.NextDelay(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(50), next);

        var capped = schedule.NextDelay(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), capped);
    }
}
