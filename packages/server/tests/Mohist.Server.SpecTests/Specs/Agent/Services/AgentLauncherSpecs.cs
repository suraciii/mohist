using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

/// <summary>
/// Specs for the shared <see cref="IAgentLauncher"/> service extracted in
/// issue-391 T-001 (originally inlined in
/// <c>AgentSessionLaunchRoutes.cs:73-97</c>). The HTTP manual launch path
/// is covered by <see cref="Api.AgentSessionLaunchRoutesSpecs"/>; this
/// file proves the launcher's per-invocation contract holds end-to-end:
/// <list type="bullet">
///   <item>
///     trigger labels are merged into the resulting session's metadata
///     labels (subscription-driven launches) — covers D6 from the
///     change design doc.
///   </item>
///   <item>
///     no <c>mohist.io/trigger/*</c> labels appear on sessions started
///     with the default trigger label (manual HTTP launch path) — covers
///     the visibility spec "Manually launched sessions carry no trigger
///     labels".
///   </item>
///   <item>
///     prompt validation rejects empty/whitespace prompts before any
///     grain call (so a partial state isn't left in the silo or DB) —
///     covers the launcher-side defense in addition to the HTTP-route
///     prompt_required gate.
///   </item>
/// </list>
/// </summary>
[Collection("LaunchIntegration")]
public class AgentLauncherSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentLauncherSpecs(IsolatedMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Launch_WithTriggerLabels_MergesThemIntoSessionMetadataLabels()
    {
        var projectId = await CreateProjectAsync("launcher-trigger-merge");
        var agent = await CreateAgentAsync(projectId, "trigger-merge-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "please review",
                new AgentLaunchContext(ProjectId: projectId, WorkspaceName: null),
                triggerLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GenericAgentSessionMetadata.TriggerEventId] = "evt_abc123",
                    [GenericAgentSessionMetadata.TriggerRuleId] = "sub_def456",
                });
        }

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.False(string.IsNullOrWhiteSpace(result.JobKey));
        Assert.Equal(agent.Id, result.AgentId);
        Assert.Equal("trigger-merge-agent", result.AgentName);

        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);
        Assert.Equal(
            "evt_abc123",
            record!.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerEventId));
        Assert.Equal(
            "sub_def456",
            record.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerRuleId));

        // Sanity: subscription-driven launch still carries the generic
        // labels that every agent-launch session has.
        Assert.Equal(agent.Id, record.Session.Metadata.Label(GenericAgentSessionMetadata.AgentId));
        Assert.Equal("trigger-merge-agent", record.Session.Metadata.Label(GenericAgentSessionMetadata.AgentName));
        Assert.Equal(projectId, record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId));
        Assert.Equal(
            "agent-launch",
            record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.SourceKind));
    }

    [Fact]
    public async Task Launch_RepeatedTrigger_ReusesStableSession()
    {
        var projectId = await CreateProjectAsync("launcher-trigger-idempotent");
        var agent = await CreateAgentAsync(projectId, "trigger-idempotent-agent");
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = "evt_repeat",
            [GenericAgentSessionMetadata.TriggerRuleId] = "sub_repeat",
        };

        AgentLaunchResult first;
        AgentLaunchResult second;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            var context = new AgentLaunchContext(ProjectId: projectId, WorkspaceName: null);
            first = await launcher.LaunchAsync(agent, "review once", context, labels);
            second = await launcher.LaunchAsync(agent, "review once", context, labels);
        }

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.StartsWith("agent-session-", first.SessionId, StringComparison.Ordinal);
        Assert.Equal(1, await CountSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_WithContextRefs_RecordsThemAsSessionMetadataLabelsOnly()
    {
        var projectId = await CreateProjectAsync("launcher-context-refs");
        var agent = await CreateAgentAsync(projectId, "context-refs-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "look at the issue",
                new AgentLaunchContext(
                    ProjectId: projectId,
                    IssueNumber: 42,
                    EpicNumber: 7,
                    Repository: "feature-repo",
                    WorkspaceName: "pay",
                    Title: null),
                triggerLabels: null);
        }

        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);
        Assert.Equal("42", record!.Session.Metadata.Label(GenericAgentSessionMetadata.IssueNumber));
        Assert.Equal("7", record.Session.Metadata.Label(GenericAgentSessionMetadata.EpicNumber));
        Assert.Equal("feature-repo", record.Session.Metadata.Label(GenericAgentSessionMetadata.Repository));
        Assert.Equal("pay", record.Session.Metadata.Label(GenericAgentSessionMetadata.WorkspaceName));

        // Context refs are prompt context only — no lifecycle labels.
        Assert.Null(record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId));
        Assert.Null(record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.SessionName));
    }

    [Fact]
    public async Task Launch_TriggerReplayAfterJobDeactivation_ReusesDurableWork()
    {
        var projectId = await CreateProjectAsync("launcher-trigger-replay");
        var agent = await CreateAgentAsync(projectId, "trigger-replay-agent");
        var eventId = "evt_trigger_replay";
        var subscriptionId = "sub_trigger_replay";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = eventId,
            [GenericAgentSessionMetadata.TriggerRuleId] = subscriptionId,
        };
        var runnerId = $"launcher-trigger-runner-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "launcher-trigger-host",
            projectId,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));

        try
        {
            AgentLaunchResult first;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                first = await launcher.LaunchAsync(
                    agent,
                    "resume this trigger",
                    new AgentLaunchContext(ProjectId: projectId, WorkspaceName: null),
                    labels);
            }

            var jobKey = TriggerJobKey(projectId, eventId, subscriptionId);
            var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
            var pending = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Pending, pending.Status);
            Assert.False(string.IsNullOrWhiteSpace(pending.RunnerId));
            var assignedRunnerId = pending.RunnerId!;
            var claim = await job.ClaimNextAsync(assignedRunnerId);
            Assert.NotNull(claim);
            var before = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Running, before.Status);
            Assert.Equal(assignedRunnerId, before.RunnerId);
            Assert.False(string.IsNullOrWhiteSpace(before.CurrentWorkId));

            await job.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

            AgentLaunchResult replay;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                replay = await launcher.LaunchAsync(
                    agent,
                    "resume this trigger",
                    new AgentLaunchContext(ProjectId: projectId, WorkspaceName: null),
                    labels);
            }

            var after = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(first.SessionId, replay.SessionId);
            Assert.Equal(before.CurrentWorkId, after.CurrentWorkId);
        }
        finally
        {
            await _fixture.Grains
                .GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task RetryService_ThreadRetryCreatesTargetedFollowupWithOriginalSlackProvenance()
    {
        var projectId = await CreateProjectAsync("agent-retry-thread");
        var agent = await CreateAgentAsync(projectId, "agent-retry-thread-agent", maxConcurrentRuns: 2);
        var origin = new ConnectionLaunchOrigin(
            "connection-thread-retry",
            "T-thread-retry",
            "U-thread-retry",
            "C-thread-retry",
            "1710000000.000001");

        AgentLaunchResult launch;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            launch = await scope.ServiceProvider.GetRequiredService<IAgentLauncher>()
                .LaunchConnectionAsync(agent, "original root", origin);
        }

        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(launch.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        Assert.NotNull(initial?.Input?.Provenance);
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "thread-retry-runtime",
            WorkDir: "/tmp/thread-retry"));
        await session.MarkInitialTurnTerminalAsync(initial!.Turn!.JobId!, AgentTurnStatus.Completed, null);

        var failedProvenance = initial.Input!.Provenance! with
        {
            ThreadId = "1710000000.000001",
            MessageId = "1710000000.000002",
            BoundThreadRootMessageId = initial.Input.Provenance.MessageId,
        };
        var failed = await session.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "original thread follow-up",
            Source: "agent-session-followup",
            IdempotencyKey: "original-thread-followup",
            Provenance: failedProvenance));
        await session.MarkFollowupTurnTerminalAsync(
            failed.OperationId,
            AgentTurnStatus.Failed,
            new AgentTurnResult(
                FailureReason: "runner unavailable",
                FailureCategory: AgentJobFailureReasons.RunnerUnavailable));

        var unrelated = await session.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "unrelated queued turn",
            Source: "agent-session-followup",
            IdempotencyKey: "unrelated-thread-followup",
            Provenance: failedProvenance with { MessageId = "1710000000.000003" }));

        var runnerId = $"thread-retry-runner-{Guid.NewGuid():N}";
        await session.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            WorkDir: "/tmp/thread-retry"));
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "thread-retry-runner",
            projectId,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));
        try
        {
            var transport = _fixture.Services.GetRequiredService<RecordingRunnerControlTransport>();
            transport.Clear();

            AgentSessionRetryResult retry;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                retry = await scope.ServiceProvider.GetRequiredService<AgentSessionRetryService>()
                    .RetryAsync(projectId, launch.SessionId, failed.TurnId, "retry-thread-click");
            }

            Assert.Equal(AgentSessionRetryOutcome.Finished, retry.Outcome);
            Assert.Equal(launch.SessionId, retry.SessionId);
            Assert.NotEqual(failed.InputId, retry.InputId);
            Assert.NotEqual(failed.TurnId, retry.TurnId);
            var followupRequest = Assert.Single(transport.Invocations, request => request.Method == "session.followup");
            var followupPayload = Assert.IsType<Mohist.Server.Contracts.FollowupParams>(followupRequest.Arguments[0]);
            Assert.Equal(retry.TurnId, followupPayload.TurnId);
            Assert.DoesNotContain(transport.Invocations, request =>
                request.Method == "session.followup"
                && request.Arguments.FirstOrDefault() is Mohist.Server.Contracts.FollowupParams payload
                && payload.TurnId == unrelated.TurnId);
            var state = await session.ListTurnsAsync();
            var failedAfter = state.Single(turn => turn.Id == failed.TurnId);
            Assert.Equal(AgentTurnStatus.Failed, failedAfter.Status);
            Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, failedAfter.Result!.FailureCategory);
            Assert.Equal("runner unavailable", failedAfter.Result.FailureReason);
            var retryInput = (await session.ListInputsAsync()).Single(input => input.Id == retry.InputId);
            Assert.Equal(AgentTurnStatus.Queued, (await session.ListTurnsAsync()).Single(turn => turn.Id == unrelated.TurnId).Status);
            Assert.Equal(failedProvenance.ConversationId, retryInput.Provenance!.ConversationId);
            Assert.Equal(failedProvenance.BoundThreadRootMessageId, retryInput.Provenance.BoundThreadRootMessageId);
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task RetryService_ThreadRetryPendingRecoveryIsIdempotent()
    {
        var projectId = await CreateProjectAsync("agent-retry-thread-recovery");
        var agent = await CreateAgentAsync(projectId, "agent-retry-thread-recovery-agent");
        var origin = new ConnectionLaunchOrigin(
            "connection-thread-recovery",
            "T-thread-recovery",
            "U-thread-recovery",
            "C-thread-recovery",
            "1710000000.000001");

        AgentLaunchResult launch;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            launch = await scope.ServiceProvider.GetRequiredService<IAgentLauncher>()
                .LaunchConnectionAsync(agent, "original root", origin);
        }

        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(launch.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "thread-recovery-runtime",
            WorkDir: "/tmp/thread-recovery"));
        await session.MarkInitialTurnTerminalAsync(initial!.Turn!.JobId!, AgentTurnStatus.Completed, null);
        var provenance = initial.Input!.Provenance! with
        {
            ThreadId = "1710000000.000001",
            MessageId = "1710000000.000002",
            BoundThreadRootMessageId = initial.Input.Provenance.MessageId,
        };
        var failed = await session.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "recover this thread follow-up",
            Source: "agent-session-followup",
            IdempotencyKey: "failed-thread-recovery",
            Provenance: provenance));
        await session.MarkFollowupTurnTerminalAsync(
            failed.OperationId,
            AgentTurnStatus.Failed,
            new AgentTurnResult(FailureCategory: AgentJobFailureReasons.RunnerUnavailable));

        var runnerId = $"thread-recovery-runner-{Guid.NewGuid():N}";
        await session.OpenAsync(new OpenAgentSessionCommand(runnerId, "opencode", WorkDir: "/tmp/thread-recovery"));
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "thread-recovery-runner",
            projectId,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));
        var transport = _fixture.Services.GetRequiredService<RecordingRunnerControlTransport>();
        transport.Clear();

        try
        {
        AgentRetryOperation operation;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            operation = (await scope.ServiceProvider.GetRequiredService<AgentRetryOperationStore>()
                .ClaimOrCreateAsync(
                    projectId,
                    launch.SessionId,
                    failed.TurnId,
                    "retry-thread-recovery",
                    AgentRetryOperationKind.Thread,
                    "recovery-session",
                    "recovery-input",
                    "recovery-turn")).Operation;
        }

        AgentSessionRetryResult first;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<AgentSessionRetryService>()
                .DispatchPendingAsync(projectId, operation.OperationId);
        }
        var inputCount = (await session.ListInputsAsync()).Count;
        var dispatchCount = transport.Invocations.Count(request => request.Method == "session.followup");

        AgentSessionRetryResult replay;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            replay = await scope.ServiceProvider.GetRequiredService<AgentSessionRetryService>()
                .DispatchPendingAsync(projectId, operation.OperationId);
        }

        Assert.Equal(AgentSessionRetryOutcome.Finished, first.Outcome);
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal(inputCount, (await session.ListInputsAsync()).Count);
        Assert.Equal(dispatchCount, transport.Invocations.Count(request => request.Method == "session.followup"));
        Assert.Equal(3, inputCount);
        await using var verify = _fixture.Services.CreateAsyncScope();
        var stored = await verify.ServiceProvider.GetRequiredService<AgentRetryOperationStore>()
            .GetAsync(projectId, operation.OperationId);
        Assert.Equal(AgentRetryOperationState.Finished, stored!.State);
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task RetryObligationWorker_ResumesPendingRootRetryWithRecordedIdentitiesExactlyOnce()
    {
        var projectId = await CreateProjectAsync("agent-retry-worker-recovery");
        var agent = await CreateAgentAsync(projectId, "agent-retry-worker-recovery-agent");
        var origin = new ConnectionLaunchOrigin(
            "connection-worker-recovery",
            "T-worker-recovery",
            "U-worker-recovery",
            "C-worker-recovery",
            "1710000000.000001");

        AgentLaunchResult failedLaunch;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            failedLaunch = await scope.ServiceProvider.GetRequiredService<IAgentLauncher>()
                .LaunchConnectionAsync(agent, "recover this root", origin);
        }

        var failedSession = _fixture.Grains.GetGrain<IAgentSessionGrain>(failedLaunch.SessionId);
        var failedInitial = await failedSession.GetInitialLaunchAsync();
        await failedSession.MarkInitialTurnTerminalAsync(
            failedInitial!.Turn!.JobId!,
            AgentTurnStatus.Failed,
            new AgentTurnResult(FailureCategory: AgentJobFailureReasons.RunnerUnavailable));

        AgentRetryOperation operation;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            operation = (await scope.ServiceProvider.GetRequiredService<AgentRetryOperationStore>()
                .ClaimOrCreateAsync(
                    projectId,
                    failedLaunch.SessionId,
                    failedInitial.Turn.Id,
                    "worker-recovery-click",
                    AgentRetryOperationKind.Root,
                    "worker-preallocated-session",
                    "worker-preallocated-input",
                    "worker-preallocated-turn")).Operation;
        }

        var worker = new AgentRetryObligationWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            _fixture.TimeProvider,
            NullLogger<AgentRetryObligationWorker>.Instance);
        await worker.ProcessPendingAsync();

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AgentRetryOperationStore>();
            var finished = await store.GetAsync(projectId, operation.OperationId);
            Assert.NotNull(finished);
            Assert.Equal(AgentRetryOperationState.Finished, finished!.State);
            Assert.Equal(operation.PreAllocatedSessionId, finished.ResultSessionId);
            Assert.Equal(operation.PreAllocatedInputId, finished.ResultInputId);
            Assert.Equal(operation.PreAllocatedTurnId, finished.ResultTurnId);
        }

        var recovered = _fixture.Grains.GetGrain<IAgentSessionGrain>(operation.PreAllocatedSessionId);
        var recoveredInitial = await recovered.GetInitialLaunchAsync();
        Assert.Equal(operation.PreAllocatedSessionId, recoveredInitial!.SessionId);
        Assert.Equal(operation.PreAllocatedInputId, recoveredInitial.Input!.Id);
        Assert.Equal(operation.PreAllocatedTurnId, recoveredInitial.Turn!.Id);
        Assert.Equal(origin.ConversationId, recoveredInitial.Input.Provenance!.ConversationId);
        Assert.Equal(origin.MessageTs, recoveredInitial.Input.Provenance.MessageId);

        // A second pass is the adapter-failover/redelivery case. The durable
        // receipt and launch idempotency key make it a no-op.
        await worker.ProcessPendingAsync();
        Assert.Single(await recovered.ListInputsAsync());
        Assert.Single(await recovered.ListTurnsAsync());

        Assert.Contains(
            _fixture.Services.GetServices<IHostedService>(),
            service => service is AgentRetryObligationWorker);
    }

    [Fact]
    public async Task RetryObligationWorker_ContinuesAfterFailureAndRetriesPendingOnNextPass()
    {
        var projectId = await CreateProjectAsync("agent-retry-worker-failure");
        AgentRetryOperation missingOperation;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            missingOperation = (await scope.ServiceProvider.GetRequiredService<AgentRetryOperationStore>()
                .ClaimOrCreateAsync(
                    projectId,
                    "missing-session",
                    "missing-turn",
                    "worker-failing-row",
                    AgentRetryOperationKind.Root,
                    "missing-new-session",
                    "missing-new-input",
                    "missing-new-turn")).Operation;
        }

        var agent = await CreateAgentAsync(projectId, "agent-retry-worker-other-agent");
        AgentLaunchResult failedLaunch;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            failedLaunch = await scope.ServiceProvider.GetRequiredService<IAgentLauncher>()
                .LaunchConnectionAsync(
                    agent,
                    "other recoverable root",
                    new ConnectionLaunchOrigin(
                        "connection-worker-other",
                        "T-worker-other",
                        "U-worker-other",
                        "C-worker-other",
                        "1710000000.000002"));
        }
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(failedLaunch.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        await session.MarkInitialTurnTerminalAsync(
            initial!.Turn!.JobId!,
            AgentTurnStatus.Failed,
            new AgentTurnResult(FailureCategory: AgentJobFailureReasons.RunnerUnavailable));

        AgentRetryOperation otherOperation;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            otherOperation = (await scope.ServiceProvider.GetRequiredService<AgentRetryOperationStore>()
                .ClaimOrCreateAsync(
                    projectId,
                    failedLaunch.SessionId,
                    initial.Turn.Id,
                    "worker-other-row",
                    AgentRetryOperationKind.Root,
                    "other-new-session",
                    "other-new-input",
                    "other-new-turn")).Operation;
        }

        var worker = new AgentRetryObligationWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            _fixture.TimeProvider,
            NullLogger<AgentRetryObligationWorker>.Instance);
        await worker.ProcessPendingAsync();

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AgentRetryOperationStore>();
            Assert.Equal(
                AgentRetryOperationState.Pending,
                (await store.GetAsync(projectId, missingOperation.OperationId))!.State);
            Assert.Equal(
                AgentRetryOperationState.Finished,
                (await store.GetAsync(projectId, otherOperation.OperationId))!.State);
        }

        // The failed row remains durable and is attempted again by the next
        // pass; it does not stop the worker or get silently discarded.
        await worker.ProcessPendingAsync();
        await using var verify = _fixture.Services.CreateAsyncScope();
        var stillPending = await verify.ServiceProvider.GetRequiredService<AgentRetryOperationStore>()
            .GetAsync(projectId, missingOperation.OperationId);
        Assert.Equal(AgentRetryOperationState.Pending, stillPending!.State);
    }

    [Fact]
    public async Task RetryObligationWorker_CleansExpiredFinishedRowsButNeverPendingRows()
    {
        var projectId = await CreateProjectAsync("agent-retry-worker-cleanup");
        AgentRetryOperation finished;
        AgentRetryOperation pending;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AgentRetryOperationStore>();
            finished = (await store.ClaimOrCreateAsync(
                projectId,
                "cleanup-finished-session",
                "cleanup-finished-turn",
                "cleanup-finished",
                AgentRetryOperationKind.Root,
                "cleanup-finished-new-session",
                "cleanup-finished-new-input",
                "cleanup-finished-new-turn")).Operation;
            await store.MarkFinishedAsync(finished.OperationId, "accepted", "done");
            pending = (await store.ClaimOrCreateAsync(
                projectId,
                "cleanup-pending-session",
                "cleanup-pending-turn",
                "cleanup-pending",
                AgentRetryOperationKind.Root,
                "cleanup-pending-new-session",
                "cleanup-pending-new-input",
                "cleanup-pending-new-turn")).Operation;
        }

        await using (var ageScope = _fixture.Services.CreateAsyncScope())
        {
            var db = ageScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var oldFinishedAt = _fixture.TimeProvider.GetUtcNow().UtcDateTime.AddHours(-25);
            var row = await db.AgentRetryOperations
                .SingleAsync(item => item.OperationId == finished.OperationId);
            row.FinishedAt = oldFinishedAt;
            row.UpdatedAt = oldFinishedAt;
            await db.SaveChangesAsync();
        }

        var worker = new AgentRetryObligationWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            _fixture.TimeProvider,
            NullLogger<AgentRetryObligationWorker>.Instance);
        await worker.ProcessPendingAsync();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var storeAfter = verify.ServiceProvider.GetRequiredService<AgentRetryOperationStore>();
        Assert.Null(await storeAfter.GetAsync(projectId, finished.OperationId));
        Assert.NotNull(await storeAfter.GetAsync(projectId, pending.OperationId));
    }

    [Fact]
    public async Task RetryService_RootRetryCommitsReceiptAndCreatesDistinctSession()
    {
        var projectId = await CreateProjectAsync("agent-retry-root");
        var agent = await CreateAgentAsync(projectId, "agent-retry-root-agent");
        var sourceOrigin = new ConnectionLaunchOrigin(
            "connection-retry",
            "T-retry",
            "U-retry",
            "C-retry",
            "1710000000.000001");

        AgentLaunchResult failedLaunch;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            failedLaunch = await launcher.LaunchConnectionAsync(agent, "retry this", sourceOrigin);
        }

        var sourceGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(failedLaunch.SessionId);
        var sourceInitial = await sourceGrain.GetInitialLaunchAsync();
        Assert.NotNull(sourceInitial?.Turn);
        Assert.NotNull(sourceInitial.Turn!.JobId);
        await sourceGrain.MarkInitialTurnTerminalAsync(
            sourceInitial.Turn.JobId!,
            AgentTurnStatus.Failed,
            new AgentTurnResult(
                Message: "runner unavailable",
                FailureReason: "runner unavailable while starting",
                FailureCategory: AgentJobFailureReasons.RunnerUnavailable));

        AgentSessionRetryResult retry;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AgentSessionRetryService>();
            retry = await service.RetryAsync(
                projectId,
                failedLaunch.SessionId,
                sourceInitial.Turn.Id,
                "retry-click-root");
        }

        Assert.Equal(AgentSessionRetryOutcome.Finished, retry.Outcome);
        Assert.NotEqual(failedLaunch.SessionId, retry.SessionId);
        Assert.NotNull(retry.OperationId);

        await using (var verify = _fixture.Services.CreateAsyncScope())
        {
            var operations = verify.ServiceProvider.GetRequiredService<AgentRetryOperationStore>();
            var operation = await operations.GetAsync(projectId, retry.OperationId!);
            Assert.NotNull(operation);
            Assert.Equal(AgentRetryOperationState.Finished, operation!.State);
            Assert.Equal(operation.PreAllocatedSessionId, retry.SessionId);
            Assert.Equal(operation.PreAllocatedInputId, retry.InputId);
            Assert.Equal(operation.PreAllocatedTurnId, retry.TurnId);
        }

        var retriedGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(retry.SessionId!);
        var retriedInitial = await retriedGrain.GetInitialLaunchAsync();
        Assert.NotNull(retriedInitial?.Input);
        Assert.NotNull(retriedInitial.Turn);
        Assert.Equal(sourceOrigin.ConnectionId, retriedInitial.Input!.Provenance!.ConnectionId);
        Assert.Equal(sourceOrigin.WorkspaceTeamId, retriedInitial.Input.Provenance.WorkspaceId);
        Assert.Equal(sourceOrigin.ConversationId, retriedInitial.Input.Provenance.ConversationId);
        Assert.Equal(sourceOrigin.MessageTs, retriedInitial.Input.Provenance.MessageId);
        Assert.Equal(sourceOrigin.SlackUserId, retriedInitial.Input.Provenance.MemberId);

        var failedAfter = await sourceGrain.GetInitialLaunchAsync();
        Assert.Equal(AgentTurnStatus.Failed, failedAfter!.Turn!.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, failedAfter.Turn.Result!.FailureCategory);
        Assert.Equal("runner unavailable while starting", failedAfter.Turn.Result.FailureReason);
    }

    [Fact]
    public async Task RetryService_RootRetryPreservesRecordedExecutionFactsAfterAgentChanges()
    {
        var projectId = await CreateProjectAsync("agent-retry-root-snapshot");
        var agent = await CreateAgentAsync(projectId, "agent-retry-root-snapshot-agent", runtime: "opencode");
        var sourceOrigin = new ConnectionLaunchOrigin(
            "connection-retry-snapshot",
            "T-retry-snapshot",
            "U-retry-snapshot",
            "C-retry-snapshot",
            "1710000000.000010");
        var attachment = new AgentSessionInputAttachmentDescriptor(
            "attachment-retry-snapshot",
            "review.txt",
            "text/plain",
            12,
            _fixture.TimeProvider.GetUtcNow());
        var startupContext = new AgentStartupContext(
            "earlier discussion",
            new AgentStartupContextProvenance("slack-history", Truncated: true, "2 oldest messages omitted", 2));

        AgentLaunchResult failedLaunch;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            failedLaunch = await launcher.LaunchConnectionAsync(
                agent,
                "retry the recorded request",
                sourceOrigin,
                workspaceName: "workspace-at-launch",
                startupContext: startupContext,
                attachments: [attachment],
                attachmentIds: [attachment.Id]);
        }

        var sourceGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(failedLaunch.SessionId);
        var sourceInitial = await sourceGrain.GetInitialLaunchAsync();
        var sourceRecord = await LoadSessionByIdAsync(failedLaunch.SessionId);
        var sourceSettings = sourceRecord!.Session.Settings;
        Assert.NotNull(sourceInitial?.Turn);
        Assert.NotNull(sourceSettings.Definition);
        await sourceGrain.MarkInitialTurnTerminalAsync(
            sourceInitial.Turn!.JobId!,
            AgentTurnStatus.Failed,
            new AgentTurnResult(
                FailureReason: "runner unavailable",
                FailureCategory: AgentJobFailureReasons.RunnerUnavailable));

        using (var update = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}",
            new
            {
                instructions = "changed after the failed launch",
                agentConfig = new { model = "openai/changed-model", runtime = "pi", variant = "fast" },
                skills = new[] { "changed-skill" },
            }))
        {
            update.EnsureSuccessStatusCode();
        }

        AgentSessionRetryResult retry;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            retry = await scope.ServiceProvider.GetRequiredService<AgentSessionRetryService>()
                .RetryAsync(projectId, failedLaunch.SessionId, sourceInitial.Turn.Id, "retry-click-root-snapshot");
        }

        Assert.Equal(AgentSessionRetryOutcome.Finished, retry.Outcome);
        var retriedGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(retry.SessionId!);
        var retried = await retriedGrain.GetInitialLaunchAsync();
        var retriedRecord = await LoadSessionByIdAsync(retry.SessionId!);
        Assert.NotNull(retried?.Input);
        Assert.NotNull(retriedRecord);
        Assert.NotNull(retriedRecord!.Session.Settings.Definition);
        Assert.Equal(sourceSettings.Definition!.Instructions, retriedRecord.Session.Settings.Definition!.Instructions);
        Assert.Equal(sourceSettings.Definition.Runtime, retriedRecord.Session.Settings.Definition.Runtime);
        Assert.Equal(sourceSettings.Definition.Model, retriedRecord.Session.Settings.Definition.Model);
        Assert.Equal(sourceSettings.Definition.Variant, retriedRecord.Session.Settings.Definition.Variant);
        Assert.Equal(sourceSettings.Definition.ReasoningEffort, retriedRecord.Session.Settings.Definition.ReasoningEffort);
        Assert.Equal(sourceSettings.Definition.Skills, retriedRecord.Session.Settings.Definition.Skills);
        Assert.Equal(sourceSettings.AgentSessionStartup!.ProjectId, retriedRecord.Session.Settings.AgentSessionStartup!.ProjectId);
        Assert.Equal(retry.SessionId, retriedRecord.Session.Settings.AgentSessionStartup.SessionId);
        Assert.Equal(sourceSettings.AgentSessionStartup.AllowedSubagents, retriedRecord.Session.Settings.AgentSessionStartup.AllowedSubagents);
        Assert.Equal(sourceSettings.AgentSessionStartup.SpawnCommand, retriedRecord.Session.Settings.AgentSessionStartup.SpawnCommand);
        Assert.Equal(sourceInitial.Input!.Attachments, retried!.Input!.Attachments);
        Assert.Equal(sourceInitial.Input.StartupContext, retried.Input.StartupContext);
        Assert.Equal(sourceInitial.Input.Text, retried.Input.Text);
        Assert.Equal(sourceOrigin.ConnectionId, retried.Input.Provenance!.ConnectionId);
    }

    [Fact]
    public async Task Launch_WithoutTriggerLabels_ProducesNoTriggerMetadataLabels()
    {
        var projectId = await CreateProjectAsync("launcher-no-trigger");
        var agent = await CreateAgentAsync(projectId, "no-trigger-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "manual trigger",
                new AgentLaunchContext(ProjectId: projectId, WorkspaceName: null),
                triggerLabels: null);
        }

        Assert.False(string.IsNullOrWhiteSpace(result.JobKey));
        Assert.StartsWith("agent-job-launch-", result.JobKey, StringComparison.Ordinal);
        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);

        // Spec requirement: manually launched sessions carry no
        // trigger metadata — neither key may appear at all (we
        // distinguish "absent" from "empty string").
        Assert.Null(record!.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerEventId));
        Assert.Null(record.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerRuleId));

        var labels = record.Session.Metadata.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.DoesNotContain(labels, kv => kv.Key.StartsWith("mohist.io/trigger/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchRouted_UsesAgentRuntimeWithoutIssueOverride()
    {
        var projectId = await CreateProjectAsync("launcher-routed-runtime-override");
        var agent = await CreateAgentAsync(projectId, "routed-runtime-override-agent", runtime: "pi");
        var eventId = $"evt-routed-runtime-override-{Guid.NewGuid():N}";
        var ruleId = "rule-routed-runtime-override";
        var sessionId = StableSessionId(projectId, eventId, ruleId);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            await launcher.LaunchRoutedAsync(
                agent,
                "use the agent runtime",
                new RoutedExecutionContext(
                    WorkflowRunId: "workflow-runtime-override",
                    ProjectId: projectId,
                    IssueNumber: 7,
                    EpicNumber: null,
                    WorkspacePath: "/tmp/routed-runtime-override",
                    TerminalRun: false),
                new CloudEvent(
                    id: eventId,
                    source: new Uri($"/mohist/issues/{projectId}/7", UriKind.Relative),
                    type: EventCatalog.ReverseDns.IssueCreated,
                    time: DateTimeOffset.UnixEpoch,
                    data: null),
                ruleId,
                ct: default);
        }

        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await session.GetAsync();
        Assert.NotNull(info);
        Assert.Equal("pi", info!.Runtime);
        var record = await LoadSessionByIdAsync(sessionId);
        Assert.Equal("event-router", record?.Session.Metadata.Label(GenericAgentSessionMetadata.Origin));
        Assert.Equal(agent.Id, record?.Session.Metadata.Label(GenericAgentSessionMetadata.TargetId));
    }

    [Fact]
    public async Task LaunchMention_ReturnsCommentAnchoredJobKey()
    {
        var projectId = await CreateProjectAsync("launcher-mention-job-key");
        var agent = await CreateAgentAsync(projectId, "mention-job-key-agent");
        const string commentId = "comment-job-key";
        const string eventId = "event-job-key";

        AgentLaunchResult result;
        string expectedJobKey;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            var resolver = scope.ServiceProvider.GetRequiredService<AgentSessionResolver>();
            expectedJobKey = resolver.CommentJobKey(projectId, commentId, agent.Id);
            result = await launcher.LaunchMentionAsync(
                agent,
                prompt: "mention launch",
                new AgentLaunchContext(ProjectId: projectId, WorkspaceName: null),
                commentId,
                eventId);
        }

        Assert.False(string.IsNullOrWhiteSpace(result.JobKey));
        Assert.Equal(expectedJobKey, result.JobKey);
    }

    [Fact]
    public async Task Launch_WithBlankPrompt_ThrowsArgumentException_WithoutAnySideEffects()
    {
        const string prompt = "   ";
        var projectId = await CreateProjectAsync("launcher-blank-prompt");
        var agent = await CreateAgentAsync(projectId, "blank-prompt-agent");

        var sessionsBefore = await CountSessionsAsync(projectId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            launcher.LaunchAsync(
                agent,
                prompt,
                new AgentLaunchContext(ProjectId: projectId, WorkspaceName: null),
                triggerLabels: null));

        var sessionsAfter = await CountSessionsAsync(projectId);
        Assert.Equal(sessionsBefore, sessionsAfter);
    }

    [Fact]
    public async Task Launch_WithNullAgent_ThrowsArgumentNullException()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            launcher.LaunchAsync(
                agent: null!,
                prompt: "any prompt",
                new AgentLaunchContext(ProjectId: "any", WorkspaceName: null),
                triggerLabels: null));
    }

    [Fact]
    public async Task Launch_WithNullContext_ThrowsArgumentNullException()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();

        var dummyAgent = new AgentInfo(
            Id: "agent_dummy",
            ProjectId: "proj_dummy",
            Name: "dummy",
            Description: "",
            Instructions: "",
            AgentConfig: null,
            Skills: Array.Empty<string>(),
            MaxConcurrentRuns: null,
            Status: AgentStatus.Active,
            CreatedAt: "2026-06-30T00:00:00Z",
            UpdatedAt: "2026-06-30T00:00:00Z");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            launcher.LaunchAsync(
                dummyAgent,
                prompt: "any prompt",
                context: null!,
                triggerLabels: null));
    }

    private async Task<int> CountSessionsAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var records = await query.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            });
        return records.Count;
    }

    private static string TriggerJobKey(string projectId, string eventId, string subscriptionId)
    {
        var identity = $"{projectId}\n{eventId}\n{subscriptionId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"agent-job-trigger-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static string StableSessionId(string projectId, string eventId, string ruleId)
    {
        var identity = $"{projectId}\n{eventId}\n{ruleId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"agent-session-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private async Task<AgentSessionRecord?> LoadSessionByIdAsync(string sessionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var records = await query.ListByIdsAsync(new[] { sessionId });
        return records.FirstOrDefault();
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"CreateProject '{name}' failed: {(int)response.StatusCode} {body}");
        }
        var bodyElement = await response.Content.ReadFromJsonAsync<JsonElement>();
        return bodyElement.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    private async Task<AgentInfo> CreateAgentAsync(string projectId, string name, string? runtime = null, int maxConcurrentRuns = 1)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                 agentConfig = runtime is null
                     ? (object)new { model = "openai/gpt-5.6" }
                     : new { model = "openai/gpt-5.6", runtime },
                skills = new[] { "coding" },
                maxConcurrentRuns,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = body.GetProperty("data").GetProperty("id").GetString()!;

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        var agent = await querier.GetByIdAsync(projectId, agentId);
        Assert.NotNull(agent);
        return agent!;
    }

}
