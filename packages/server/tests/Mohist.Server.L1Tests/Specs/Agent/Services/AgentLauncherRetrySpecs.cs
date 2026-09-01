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
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Agent.Services;

/// <summary>
/// Retry-path launcher specs split from <see cref="AgentLauncherSpecs"/> to
/// keep both files within the file-size ratchet.
/// </summary>
[Collection("LaunchIntegrationB")]
public class AgentLauncherRetrySpecs : AgentLauncherSupportSpecs
{
    protected MohistIntegrationFixture _fixture => Fixture;

    public AgentLauncherRetrySpecs(IsolatedMohistIntegrationFixture fixture)
        : base(fixture)
    {
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
        var replyProvenance = new AgentSessionInputProvenance(
            "slack",
            origin.WorkspaceTeamId,
            origin.ConversationId,
            "1710000000.000002",
            origin.SlackUserId,
            "1710000000.000003",
            origin.ConnectionId,
            "1710000000.000002");

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
                    "worker-preallocated-turn",
                    replyProvenance)).Operation;
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
            Assert.Equal(replyProvenance, finished.ReplyProvenance);
        }

        var recovered = _fixture.Grains.GetGrain<IAgentSessionGrain>(operation.PreAllocatedSessionId);
        var recoveredInitial = await recovered.GetInitialLaunchAsync();
        Assert.Equal(operation.PreAllocatedSessionId, recoveredInitial!.SessionId);
        Assert.Equal(operation.PreAllocatedInputId, recoveredInitial.Input!.Id);
        Assert.Equal(operation.PreAllocatedTurnId, recoveredInitial.Turn!.Id);
        Assert.Equal(origin.ConversationId, recoveredInitial.Input.Provenance!.ConversationId);
        Assert.Equal(replyProvenance.MessageId, recoveredInitial.Input.Provenance.MessageId);
        Assert.Equal(replyProvenance.BoundThreadRootMessageId, recoveredInitial.Input.Provenance.BoundThreadRootMessageId);

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
                agentSessionStartup: new AgentSessionStartup(
                    projectId,
                    "source-startup-session",
                    ParentSessionId: null,
                    AllowedSubagents: [],
                    SpawnCommand: "mo agent spawn original"),
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
        Assert.Equal("mo agent spawn original", retriedRecord.Session.Settings.AgentSessionStartup.SpawnCommand);
        Assert.Equal(sourceInitial.Input!.Attachments, retried!.Input!.Attachments);
        Assert.Equal(sourceInitial.Input.StartupContext, retried.Input.StartupContext);
        Assert.Equal(sourceInitial.Input.Text, retried.Input.Text);
        Assert.Equal(sourceOrigin.ConnectionId, retried.Input.Provenance!.ConnectionId);
    }

}
