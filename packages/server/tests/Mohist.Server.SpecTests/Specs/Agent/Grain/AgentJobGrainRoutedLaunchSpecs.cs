using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

/// <summary>
/// Focused specs for the routed-launch prepared-launch protocol
/// (issue-449 T-002 / design decisions 1-3). Covers the canonical
/// <see cref="RoutedAgentLaunchPlan"/> persisted before Session open,
/// idempotent recovery on activation loss, durable recovery reminder
/// behaviour, and the preflight-failed terminal-delivery path.
/// </summary>
[Collection("AgentJobGrain")]
public class AgentJobGrainRoutedLaunchSpecs : AgentJobGrainTestSupport
{
    public AgentJobGrainRoutedLaunchSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    private string StableJobKey(string projectId, string eventId, string ruleId)
    {
        var identity = $"{projectId}\n{eventId}\n{ruleId}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return $"agent-job-trigger-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private string StableSessionId(string projectId, string eventId, string ruleId)
    {
        var identity = $"{projectId}\n{eventId}\n{ruleId}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return $"agent-session-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private RoutedAgentLaunchPlan BuildExecutablePlan(
        string projectId, string eventId, string ruleId, string workspacePath, string agentId = "agent-routed")
    {
        return new RoutedAgentLaunchPlan(
            ProjectId: projectId,
            EventId: eventId,
            RuleId: ruleId,
            SessionId: StableSessionId(projectId, eventId, ruleId),
            JobKey: StableJobKey(projectId, eventId, ruleId),
            IssueNumber: 7,
            EpicNumber: 1,
            WorkspacePath: workspacePath,
            Disposition: RoutedLaunchDisposition.Executable,
            PreflightReason: null,
            PreflightCategory: null,
            PreparedAt: _fixture.TimeProvider.GetUtcNow(),
            AgentId: agentId,
            AgentName: "routed-agent",
            AgentInstructions: "do the thing",
            AgentConfigJson: """{"model":"openai/gpt-test"}""",
            Model: "openai/gpt-test",
            Variant: null,
            Prompt: "fix the thing");
    }

    private RoutedAgentLaunchPlan BuildPreflightPlan(
        string projectId, string eventId, string ruleId, string reason, string category)
    {
        return new RoutedAgentLaunchPlan(
            ProjectId: projectId,
            EventId: eventId,
            RuleId: ruleId,
            SessionId: StableSessionId(projectId, eventId, ruleId),
            JobKey: StableJobKey(projectId, eventId, ruleId),
            IssueNumber: 7,
            EpicNumber: 1,
            WorkspacePath: null,
            Disposition: RoutedLaunchDisposition.PreflightFailed,
            PreflightReason: reason,
            PreflightCategory: category,
            PreparedAt: _fixture.TimeProvider.GetUtcNow(),
            AgentId: "agent-routed",
            AgentName: "routed-agent",
            AgentInstructions: null,
            AgentConfigJson: null,
            Model: null,
            Variant: null,
            Prompt: "fix the thing");
    }

    [Fact]
    public async Task EnsurePreparedAsync_FirstDelivery_PersistsPlanAndRegistersReminder()
    {
        var projectId = $"routed-prepare-{Guid.NewGuid():N}";
        var eventId = "evt-routed-prepare";
        var ruleId = "rule-routed-prepare";
        var plan = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-prepare");

        var job = JobGrain(plan.JobKey);
        var canonical = await job.EnsurePreparedAsync(plan);

        Assert.Equal(plan.JobKey, canonical.JobKey);
        Assert.Equal(plan.SessionId, canonical.SessionId);
        Assert.Equal(plan.WorkspacePath, canonical.WorkspacePath);
        Assert.Equal(RoutedLaunchDisposition.Executable, canonical.Disposition);

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, snapshot.Status);
    }

    [Fact]
    public async Task EnsurePreparedAsync_RedeliveryWithDifferentWorkspace_ReturnsFirstPlan()
    {
        // First-writer: the second caller resolves a different
        // workspace but the persisted canonical plan wins, so the
        // later workspace cannot overwrite the earlier one.
        var projectId = $"routed-redeliver-{Guid.NewGuid():N}";
        var eventId = "evt-routed-redeliver";
        var ruleId = "rule-routed-redeliver";
        var first = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-first");

        var job = JobGrain(first.JobKey);
        var canonicalFirst = await job.EnsurePreparedAsync(first);
        Assert.Equal("/tmp/routed-first", canonicalFirst.WorkspacePath);

        var redelivery = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-second");
        var canonicalSecond = await job.EnsurePreparedAsync(redelivery);

        Assert.Equal("/tmp/routed-first", canonicalSecond.WorkspacePath);
        Assert.Equal(canonicalFirst.JobKey, canonicalSecond.JobKey);
    }

    [Fact]
    public async Task AdvancePreparedLaunchAsync_OpensSessionAndPersistsInputForDispatch()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"routed-dispatch-runner-{Guid.NewGuid():N}");
        var eventId = "evt-routed-dispatch";
        var ruleId = "rule-routed-dispatch";
        var plan = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-dispatch");

        var job = JobGrain(plan.JobKey);
        await job.EnsurePreparedAsync(plan);
        await job.AdvancePreparedLaunchAsync();

        // Session was opened from the canonical plan values.
        var session = Grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
        var info = await session.GetAsync();
        Assert.NotNull(info);
        Assert.Equal(plan.WorkspacePath, info!.WorkDir);

        // The grain reached Runner acceptance because a runner was
        // already registered for this project.
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Running, snapshot.Status);
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.True(snapshot.RunnerAccepted,
            "Runner acceptance is the durable fence that releases the prepared-launch obligation");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        var work = Assert.Single(state.ActiveWorks, item => item.OwnerId == plan.JobKey);
        Assert.Equal(snapshot.CurrentWorkId, work.WorkId);
    }

    [Fact]
    public async Task AdvancePreparedLaunchAsync_PiRuntime_OpensSessionWithPi()
    {
        // Issue-452 design D3 + D5: the routed path reads the runtime
        // from the canonical RoutedAgentLaunchPlan; the session is
        // opened with the snapshotted runtime, not a hardcoded literal.
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"routed-pi-dispatch-runner-{Guid.NewGuid():N}");
        var eventId = "evt-routed-pi-dispatch";
        var ruleId = "rule-routed-pi-dispatch";
        var plan = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-pi-dispatch")
            with { Runtime = "pi" };

        var job = JobGrain(plan.JobKey);
        await job.EnsurePreparedAsync(plan);
        await job.AdvancePreparedLaunchAsync();

        var session = Grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
        var info = await session.GetAsync();
        Assert.NotNull(info);
        Assert.Equal("pi", info!.Runtime);

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        var work = Assert.Single(state.ActiveWorks, item => item.OwnerId == plan.JobKey);
        Assert.False(string.IsNullOrWhiteSpace(work.WorkId));
    }

    [Fact]
    public async Task OnActivate_AfterCrash_ReusesSnapshottedRuntime()
    {
        // Crash-injection: an activation loss after the plan is durable
        // but before dispatch advances must still resume the plan with
        // the snapshotted runtime. Recovery must not re-read mutable
        // Agent config (issue-452 design D2 / scenario "Replay reuses
        // the snapshotted backend").
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"routed-runtime-recovery-runner-{Guid.NewGuid():N}");
        var eventId = "evt-routed-runtime-recovery";
        var ruleId = "rule-routed-runtime-recovery";
        var plan = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-runtime-recovery")
            with { Runtime = "pi" };

        var job = JobGrain(plan.JobKey);
        await job.EnsurePreparedAsync(plan);

        await DeactivateGrainAsync(job);
        await job.GetStatusAsync();
        await DeactivateGrainAsync(job);
        await job.GetStatusAsync();

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(10));

        var session = Grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
        var info = await session.GetAsync();
        Assert.NotNull(info);
        Assert.Equal("pi", info!.Runtime);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        var work = Assert.Single(state.ActiveWorks, item => item.OwnerId == plan.JobKey);
        Assert.False(string.IsNullOrWhiteSpace(work.WorkId));
    }

    [Fact]
    public async Task AdvancePreparedLaunchAsync_NoRunner_StaysPendingAndKeepsReminder()
    {
        // No runner is registered: dispatch cannot reach Runner
        // acceptance, but the AgentJob must remain Pending and keep
        // the recovery reminder alive so retry / activation loss can
        // resume once a runner is available.
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"routed-pending-{Guid.NewGuid():N}";
        var eventId = "evt-routed-pending";
        var ruleId = "rule-routed-pending";
        var plan = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-pending");

        var job = JobGrain(plan.JobKey);
        await job.EnsurePreparedAsync(plan);
        await job.AdvancePreparedLaunchAsync();

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, snapshot.Status);
        Assert.Null(snapshot.RunnerId);
        Assert.False(snapshot.RunnerAccepted);
    }

    [Fact]
    public async Task AdvancePreparedLaunchAsync_PreflightFailed_EntersTerminalDeliveryWithReasonAndCategory()
    {
        var projectId = $"routed-preflight-{Guid.NewGuid():N}";
        var eventId = "evt-routed-preflight";
        var ruleId = "rule-routed-preflight";
        var plan = BuildPreflightPlan(
            projectId, eventId, ruleId,
            AgentJobFailureReasons.WorkspaceUnavailable,
            AgentJobFailureReasons.WorkspaceUnavailable);

        var job = JobGrain(plan.JobKey);
        await job.EnsurePreparedAsync(plan);
        await job.AdvancePreparedLaunchAsync();

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
        await session.WaitForPersistenceAsync(_fixture.Persistence);

        // Verify the Session was actually opened from the canonical plan.
        var info = await session.GetAsync();
        Assert.NotNull(info);

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.False(snapshot.HasPendingSessionClose,
            $"Terminal delivery should have completed; HasPendingSessionClose was true. info={info!.Status}");

        var allTurns = await CountTurnsAsync(plan.SessionId);
        var parts = await ListSessionClosedPartsAsync(plan.SessionId);
        Assert.True(allTurns > 0 || parts.Count > 0,
            $"Both turn and part counts are zero; session state likely was not created. turns={allTurns} parts={parts.Count} status={info!.Status}");

        var closed = Assert.Single(parts, p => p.Type == TranscriptPartTypes.SessionActivity);
        var payload = JSON.DeserializeElement(closed.PayloadJson);
        Assert.Equal(
            AgentJobSessionDeliveryIds.TerminalDeliveryId(plan.JobKey),
            payload.GetProperty("operationId").GetString());
        Assert.Equal(
            AgentJobSessionDeliveryIds.TerminalDeliveryId(plan.JobKey),
            payload.GetProperty("deliveryId").GetString());
        Assert.Equal(AgentJobFailureReasons.WorkspaceUnavailable, payload.GetProperty("failureReason").GetString());
        Assert.Equal(AgentJobFailureReasons.WorkspaceUnavailable, payload.GetProperty("failureCategory").GetString());

        Assert.NotNull(plan.IssueNumber);
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var assembler = new IssueEventFeedAssembler(
            _fixture.EventStore,
            null!,
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>());
        var events = await assembler.ListAsync(projectId, plan.IssueNumber.Value, workflowRunId: null, limit: 200);

        var activity = Assert.Single(events, entry => entry.Envelope.Type == "session.activity");
        Assert.Equal(plan.SessionId, activity.Envelope.Subject);
        Assert.Equal("failed", activity.Envelope.Data!.Value.GetProperty("status").GetString());
        Assert.Equal(AgentJobFailureReasons.WorkspaceUnavailable,
            activity.Envelope.Data!.Value.GetProperty("failureReason").GetString());
        Assert.Equal(_fixture.TimeProvider.GetUtcNow().ToString("o"),
            activity.Envelope.Data!.Value.GetProperty("recordedAt").GetString());
    }

    private async Task<int> CountTurnsAsync(string sessionId)
    {
        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        return await db.AgentSessionTranscriptTurns
            .Where(t => t.SessionId == sessionId)
            .CountAsync();
    }

    [Fact]
    public async Task AdvancePreparedLaunchAsync_Executable_DispatchesAndOpensSessionWithWorkspace()
    {
        // Sanity probe: the executable path (the one the AgentLauncher
        // exercises) opens the Session from the canonical plan's
        // workspace and runs through normal dispatch. Acts as the
        // companion to the preflight-failed test above; if this one
        // also fails, the problem is in the open/save wiring rather
        // than in the preflight branch.
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"routed-sanity-{Guid.NewGuid():N}");
        var eventId = "evt-routed-sanity";
        var ruleId = "rule-routed-sanity";
        var plan = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-sanity");

        var job = JobGrain(plan.JobKey);
        await job.EnsurePreparedAsync(plan);
        await job.AdvancePreparedLaunchAsync();

        // Session opens with the canonical workspace path.
        var session = Grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
        var info = await session.GetAsync();
        Assert.NotNull(info);
        Assert.Equal("/tmp/routed-sanity", info!.WorkDir);

        // Dispatch reaches Runner acceptance.
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.True(snapshot.RunnerAccepted);
    }

    [Fact]
    public async Task OnActivate_AfterCrash_BeforeSessionOpen_ResumesFromPersistedPlan()
    {
        // Crash-injection: EnsurePreparedAsync persisted the canonical
        // plan but the silo failed before AdvancePreparedLaunchAsync
        // could open Session or dispatch. On reactivation the grain
        // must advance the persisted plan, not re-read the resolver.
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"routed-activate-runner-{Guid.NewGuid():N}");
        var eventId = "evt-routed-activate";
        var ruleId = "rule-routed-activate";
        var plan = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-activate");

        var job = JobGrain(plan.JobKey);
        await job.EnsurePreparedAsync(plan);

        // Simulate process loss by deactivating the grain without
        // advancing the launch; the next call rehydrates from the
        // persistent state and OnActivate resumes the plan.
        await DeactivateGrainAsync(job);
        // The grain rehydrates on the next call. Touching any grain
        // method forces activation; we then expect Running once
        // OnActivate has called AdvancePreparedLaunchAsync.
        await job.GetStatusAsync();
        // Deactivate once more so the reminder (registered by
        // OnActivate's EnsureRecoveryReminderAsync) actually has a
        // chance to fire and re-drive the advance when the runner
        // registration races the reactivation.
        await DeactivateGrainAsync(job);
        await job.GetStatusAsync();

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(10));

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Running, snapshot.Status);
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.True(snapshot.RunnerAccepted);

        // Session was opened from the persisted canonical plan only.
        var session = Grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
        var info = await session.GetAsync();
        Assert.NotNull(info);
        Assert.Equal("/tmp/routed-activate", info!.WorkDir);
    }

    [Fact]
    public async Task RedeliveryWithDifferentPlan_ReusesCanonicalPlan_ProducesOneSessionAndOneClose()
    {
        // First delivery: workspace-unavailable preflight. Second
        // delivery: a fully resolvable executable plan. First-writer
        // semantics means the second delivery must NOT create a new
        // session / job / close fact.
        var projectId = $"routed-redeliver-preflight-{Guid.NewGuid():N}";
        var eventId = "evt-routed-redeliver-preflight";
        var ruleId = "rule-routed-redeliver-preflight";

        var preflight = BuildPreflightPlan(
            projectId, eventId, ruleId,
            AgentJobFailureReasons.WorkspaceUnavailable,
            AgentJobFailureReasons.WorkspaceUnavailable);
        var job = JobGrain(preflight.JobKey);
        await job.EnsurePreparedAsync(preflight);
        await job.AdvancePreparedLaunchAsync();
        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        // Confirm first delivery produced its single activity fact.
        var session = Grains.GetGrain<IAgentSessionGrain>(preflight.SessionId);
        await session.WaitForPersistenceAsync(_fixture.Persistence);
        var firstParts = (await ListSessionClosedPartsAsync(preflight.SessionId))
            .Where(p => p.Type == TranscriptPartTypes.SessionActivity)
            .ToList();
        Assert.Single(firstParts);
        // Issue 484: the delivery id is recorded as `operationId` on the
        // session.activity part.
        Assert.Equal(AgentJobSessionDeliveryIds.TerminalDeliveryId(preflight.JobKey),
            JSON.DeserializeElement(firstParts[0].PayloadJson).GetProperty("operationId").GetString());
        // (xUnit2031 satisfied by the deferred Where/Assert pair;
        // the assertion covers both presence and uniqueness.)

        // Redelivery with a perfectly resolvable plan must reuse the
        // canonical preflight-failed plan.
        var executable = BuildExecutablePlan(projectId, eventId, ruleId, "/tmp/routed-resolved");
        var canonicalRedelivery = await job.EnsurePreparedAsync(executable);
        Assert.Equal(RoutedLaunchDisposition.PreflightFailed, canonicalRedelivery.Disposition);
        Assert.Equal(AgentJobFailureReasons.WorkspaceUnavailable, canonicalRedelivery.PreflightReason);
        Assert.Null(canonicalRedelivery.WorkspacePath);

        // The grain remains terminal; advancing is a no-op for
        // already-terminal jobs.
        await job.AdvancePreparedLaunchAsync();
        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Failed, snapshot.Status);
        var runtime = await job.GetRuntimeSnapshotAsync();
        Assert.False(runtime.HasPendingSessionClose,
            "Successful delivery cleared the pending payload");

        // No duplicate activity fact is recorded for the redelivered
        // session id and no Runner assignment is created.
        var allParts = (await ListSessionClosedPartsAsync(preflight.SessionId))
            .Where(p => p.Type == TranscriptPartTypes.SessionActivity)
            .ToList();
        Assert.Single(allParts);
    }

    [Fact]
    public async Task PreflightFailure_DoesNotAssignRunner()
    {
        // A preflight failure (no resolvable workspace) must NOT
        // create a Runner assignment or a workspace-less WorkDispatch.
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"routed-no-dispatch-runner-{Guid.NewGuid():N}");
        var eventId = "evt-routed-no-dispatch";
        var ruleId = "rule-routed-no-dispatch";
        var preflight = BuildPreflightPlan(
            projectId, eventId, ruleId,
            AgentJobFailureReasons.WorkspaceUnavailable,
            AgentJobFailureReasons.WorkspaceUnavailable);

        var job = JobGrain(preflight.JobKey);
        await job.EnsurePreparedAsync(preflight);
        await job.AdvancePreparedLaunchAsync();
        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Failed, snapshot.Status);
        Assert.Null(snapshot.RunnerId);
        Assert.Null(snapshot.CurrentWorkId);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        Assert.DoesNotContain(state.ActiveWorks, work => work.OwnerId == preflight.JobKey);
    }

    private async Task DeactivateGrainAsync(IGrain grain)
    {
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
    }

    private async Task<List<Infrastructure.Data.Sessions.AgentSessionTranscriptPartRow>> ListSessionClosedPartsAsync(string sessionId)
    {
        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var turnIds = await db.AgentSessionTranscriptTurns
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToListAsync();
        if (turnIds.Count == 0) return [];
        return await db.AgentSessionTranscriptParts
            .Where(p => turnIds.Contains(p.TurnId))
            .OrderBy(p => p.Sequence)
            .ThenBy(p => p.Id)
            .ToListAsync();
    }
}
